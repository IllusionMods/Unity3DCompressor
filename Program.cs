using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Extra.Decompressors.LZ4;
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

        private static bool SkipMetadata = false;

        private static bool Verbose = false;

        private static readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();

        private static void Main(string[] args)
        {
            var switches = args.Where(x => x.StartsWith("--")).ToArray();
            args = args.Except(switches).ToArray();

            if (args.Length == 0)
            {
                Console.WriteLine($"Drag and drop Unity asset bundles or folders containing asset bundles on to this .exe to compress or decompress them.");
                Console.WriteLine($"  - If compressed (LZ4) -> Decompresses");
                Console.WriteLine($"  - If uncompressed (None)  -> Compresses (LZ4)");
                Console.WriteLine($"Use '--skipMetadata' switch to skip creating metadata files used to reconstruct original hash");
                Console.WriteLine($"Use '--randomizeCAB' switch to randomize CAB strings of compressed bundles. This will skip creating metadata files.");
                Console.WriteLine($"Press any key to exit.");
                WaitForKey();
                return;
            }

            foreach (var switchStr in switches)
            {
                switch (switchStr.ToLowerInvariant())
                {
                    case "--skipmetadata":
                        SkipMetadata = true;
                        break;
                    case "--randomizecab":
                        CABRandomization = true;
                        SkipMetadata = true;
                        break;
                    case "--verbose":
                        Verbose = true;
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
                    ProcessFile(path);
                }
                else if (Directory.Exists(path))
                {
                    var allfiles = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                    foreach (var x in allfiles)
                    {
                        ProcessFile(x);
                    }
                }
                else
                {
                    Console.WriteLine("Skipping invalid path: " + path);
                }
            }

            Console.Write($"Finished processing asset bundles. Press any key to exit.");
            WaitForKey();
        }

        /// <summary>
        /// Wait for a keypress when running in an interactive terminal.
        /// Console.ReadKey throws if stdin is redirected (e.g. when launched from a script or file manager), so skip it.
        /// </summary>
        private static void WaitForKey()
        {
            if (!Console.IsInputRedirected)
                Console.ReadKey();
        }

        public static bool ProcessFile(string file)
        {
            if (!FileIsAssetBundle(file))
            {
                Console.WriteLine($"Skipping {file}, not an asset bundle.");
                return false;
            }

            try
            {
                // retrieve file compression type
                var assetsManager = new AssetsManager();
                var bundle = assetsManager.LoadBundleFile(file);
                var compressionType = bundle.file.GetCompressionType();
                bool isCompressed = compressionType != AssetBundleCompressionType.None;
                var targetCompression = isCompressed
                    ? AssetBundleCompressionType.None
                    : AssetBundleCompressionType.LZ4;

                var actionName = isCompressed
                    ? "Decompressing"
                    : "Compressing";

                if (!isCompressed)
                {
                    if (!SkipMetadata)
                        SaveMetadata(file, bundle.file);
                    if (CABRandomization)
                        RandomizeCAB(file);
                }
                else
                {
                    bundle.file.DataReader = new AssetsFileReader(DecompressLz4Blocks(bundle.file));
                }

                Console.WriteLine($"{actionName} {file}");

                // LZMA bundles are unpacked on load, LZ4 blocks are decoded on the fly,
                // so DataReader always exposes the plain serialized data here.
                string temp = file + ".temp";
                try
                {
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new AssetsFileWriter(stream))
                        bundle.file.Pack(writer, targetCompression);

                    assetsManager.UnloadAllBundleFiles();

                    if (isCompressed && !CABRandomization)
                        TryRestoreOriginal(file, temp);

                    File.Move(temp, file, true);
                }
                catch
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file, skipping ({ex.Message})");
                if (Verbose)
                    Console.WriteLine(ex);
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
            string ext = Path.GetExtension(path);
            if (ext.Equals(".meta", StringComparison.OrdinalIgnoreCase) || ext.Equals(".temp", StringComparison.OrdinalIgnoreCase))
                return false;

            if (ext == ".unity3d")
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

        /// <summary>
        /// Save metadata used to reconstruct original file with original hash
        /// </summary>
        /// <param name="file"></param>
        /// <param name="bundleFile"></param>
        private static void SaveMetadata(string file, AssetBundleFile bundleFile)
        {
            long dataOffset = bundleFile.Header.GetFileDataOffset();
            var bytes = File.ReadAllBytes(file);

            using var fs = File.Create(file + ".meta");
            using var bw = new BinaryWriter(fs);
            bw.Write(SHA256.HashData(bytes));      // 32 bytes: hash of the full original
            bw.Write((long)bytes.Length);
            bw.Write((int)dataOffset);
            bw.Write(bytes, 0, (int)dataOffset);   // original header + block/dir info
        }

        /// <summary>
        /// Restore original file if metadata available
        /// </summary>
        /// <param name="file"></param>
        /// <param name="tempFile"></param>
        private static void TryRestoreOriginal(string file, string tempFile)
        {
            string meta = file + ".meta";
            if (!File.Exists(meta))
            {
                Console.WriteLine($"  No metadata for {Path.GetFileName(file)}; hash cannot be restored.");
                return;
            }

            byte[] origHash, prefix;
            long origLength;
            using (var br = new BinaryReader(File.OpenRead(meta)))
            {
                origHash = br.ReadBytes(32);
                origLength = br.ReadInt64();
                int prefixLen = br.ReadInt32();
                prefix = br.ReadBytes(prefixLen);
            }

            // locate the data region in the freshly unpacked temp file
            long newOffset, dataLength = 0;
            var nb = new AssetBundleFile();
            using (var fs = File.OpenRead(tempFile))
            {
                nb.Read(new AssetsFileReader(fs));
                newOffset = nb.Header.GetFileDataOffset();
                foreach (var b in nb.BlockAndDirInfo.BlockInfos)
                    dataLength += b.DecompressedSize;
            }

            var newBytes = File.ReadAllBytes(tempFile);
            var rebuilt = new byte[prefix.Length + dataLength];
            Buffer.BlockCopy(prefix, 0, rebuilt, 0, prefix.Length);
            Buffer.BlockCopy(newBytes, (int)newOffset, rebuilt, prefix.Length, (int)dataLength);

            if (rebuilt.Length == origLength && SHA256.HashData(rebuilt).AsSpan().SequenceEqual(origHash))
            {
                File.WriteAllBytes(tempFile, rebuilt);
                File.Delete(meta);
                Console.WriteLine("Restored original bytes");
            }
            else
            {
                Console.WriteLine("Could not reproduce the original exactly; keeping repacked output.");
            }
        }

        /// <summary>
        /// Decompresses LZ4 blocks (work around for AssetsTools.NET.LZ4BlockStream.Read reading beyond the end of the stream and throwing an exception)
        /// </summary>
        /// <param name="bf"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        private static MemoryStream DecompressLz4Blocks(AssetBundleFile bf)
        {
            var src = bf.Reader.BaseStream;
            src.Position = bf.Header.GetFileDataOffset();

            var output = new MemoryStream();
            foreach (var info in bf.BlockAndDirInfo.BlockInfos)
            {
                var comp = new byte[info.CompressedSize];
                src.ReadExactly(comp);

                switch (info.GetCompressionType())
                {
                    case 0:
                        output.Write(comp);
                        break;
                    case 2:
                    case 3:
                        {
                            var data = new byte[info.DecompressedSize];
                            using var decoder = new Lz4DecoderStream(new MemoryStream(comp));
                            decoder.ReadExactly(data);
                            output.Write(data);
                            break;
                        }
                    default:
                        throw new NotSupportedException($"Block compression type {info.GetCompressionType()} not handled.");
                }
            }
            output.Position = 0;
            return output;
        }
    }
}
