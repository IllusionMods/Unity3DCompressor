using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Unity3DCompressor
{
    internal class Program
    {
        //Randomizing CAB-strings of game asset bundles can break their dependencies, only use this if you know what you're doing.
        private static bool CABRandomization = false;

        private static readonly RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();

        private static void Main(string[] args)
        {
            var switches = args.Where(x => x.StartsWith("--")).ToArray();
            args = args.Except(switches).ToArray();

            if (args.Length == 0)
            {
                Console.WriteLine($"Drag and drop Unity asset bundles or folders containing asset bundles on to this .exe to compress them.");
                Console.WriteLine($"Use '--randomizeCAB' switch to randomize CAB strings of compressed bundles. Do not use on game bundles or other bundles with dependencies.");
                Console.WriteLine($"Press any key to exit.");
                Console.ReadKey();
                return;
            }

            foreach (var switchStr in switches)
            {
                switch (switchStr.ToLowerInvariant())
                {
                    case "--randomizecab":
                        CABRandomization = true;
                        break;
                    default:
                        Console.WriteLine("Ignoring unknown switch: " + switchStr);
                        break;
                }
            }

            foreach (string path in args)
            {
                if (File.Exists(path))
                {
                    CompressFile(path);
                }
                else if (Directory.Exists(path))
                {
                    var allfiles = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                    foreach (var x in allfiles)
                    {
                        CompressFile(x);
                    }
                }
                else
                {
                    Console.WriteLine("Skipping invalid path: " + path);
                }
            }

            Console.Write($"Finished compressing asset bundles. Press any key to exit.");
            Console.ReadKey();
        }

        public static bool CompressFile(string file)
        {
            if (!FileIsAssetBundle(file))
            {
                Console.WriteLine($"Skipping {file}, not an asset bundle.");
                return false;
            }

            if (CABRandomization)
                RandomizeCAB(file);

            try
            {
                Console.WriteLine($"Compressing {file}");

                var assetsManager = new AssetsManager();
                var bundle = assetsManager.LoadBundleFile(file);
                using (var stream = File.OpenWrite(file + ".temp"))
                using (var writer = new AssetsFileWriter(stream))
                    bundle.file.Pack(bundle.file.reader, writer, AssetBundleCompressionType.LZ4);

                File.Move(file + ".temp", file, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error compressing, skipping ({ex.Message})");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Check if the file is an asset bundle
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private static bool FileIsAssetBundle(string path)
        {
            if (Path.GetExtension(path) == ".unity3d")
                return true;

            byte[] buffer = new byte[7];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                fs.Read(buffer, 0, buffer.Length);
                fs.Close();
            }
            return Encoding.UTF8.GetString(buffer, 0, buffer.Length) == "UnityFS";
        }

        /// <summary>
        /// Open the file, randomize the CAB-string, and save it
        /// </summary>
        /// <param name="file"></param>
        internal static void RandomizeCAB(string file)
        {
            var assetBundleData = File.ReadAllBytes(file);
            var ascii = Encoding.ASCII.GetString(assetBundleData, 0, Math.Min(1024, assetBundleData.Length - 4));
            var origCabIndex = ascii.IndexOf("CAB-", StringComparison.Ordinal);

            if (origCabIndex < 0)
                return;

            var origCabLength = ascii.Substring(origCabIndex).IndexOf('\0');

            if (origCabLength < 0)
                return;

            var CAB = GenerateCAB().Substring(4);
            var cabBytes = Encoding.ASCII.GetBytes(CAB);

            if (origCabLength > 36)
                return;

            Buffer.BlockCopy(cabBytes, 36 - origCabLength, assetBundleData, origCabIndex + 4, origCabLength - 4);

            File.WriteAllBytes(file, assetBundleData);
        }

        /// <summary>
        /// Generate a new CAB-string
        /// </summary>
        /// <returns></returns>
        internal static string GenerateCAB()
        {
            var rnbuf = new byte[16];
            rng.GetBytes(rnbuf);
            return "CAB-" + string.Concat(rnbuf.Select((x) => ((int)x).ToString("X2")).ToArray()).ToLower();
        }
    }
}
