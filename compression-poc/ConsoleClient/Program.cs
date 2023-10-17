
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Amazon.S3;
using Amazon.S3.Transfer;
using Philips.Platform.Common.DataAccess;
using Philips.Platform.CommonUtilities.Pooling;
using Philips.Platform.Dicom;

using MultiframeImageWriter = Compressor.MultiframeImageWriter;

namespace ConsoleClient;

public static class Program
{

    /// <summary>
    /// arg[0] = Thread count
    /// arg[1] = File count 
    /// arg[2] = Enable compression
    /// arg[3] = Execution time in minutes
    /// </summary>
    /// <param name="args"></param>
    /// <exception cref="ArgumentException"></exception>
    public static void Main(string[] args)
    {
        //args = new string[5];
        //args[0] = "10";
        //args[1] = "30";
        //args[2] = "true";
        //args[3] = "10";
        //args[4] = "TestImages";
        int executionTimeInMinutes;
        if (!int.TryParse(args[0], out int threadCount))
        {
            throw new ArgumentException("Invalid thread count args[0].");
        }
        if (!int.TryParse(args[1], out int requestedFileCount))
        {
            throw new ArgumentException("Invalid file count args[1].");
        }
        if (!bool.TryParse(args[2], out bool needCompression))
        {
            throw new ArgumentException("Param enable compression is not correct args[2].");
        }
        else if (!int.TryParse(args[3], out executionTimeInMinutes))
        {
            throw new ArgumentException("Invalid execution time.");
        }
        string inputFolderName = "";
        if (string.IsNullOrEmpty(args[4]))
        {
            throw new ArgumentException("Folder path.");
        }
        else
        {
            inputFolderName = args[4];
        }

        string[] testDataCollection = Directory.GetFiles(@inputFolderName, "*.dcm",
              SearchOption.AllDirectories);

        if (requestedFileCount > testDataCollection.Count())
        {
            throw new ArgumentException($"Total file count allowed {testDataCollection.Count()}.");
        }

        string[] requestedFiles = new string[requestedFileCount];
        Array.Copy(testDataCollection, requestedFiles, requestedFileCount);

        var client = new CompressionClient();
        client.StartProcess(threadCount, needCompression, executionTimeInMinutes, requestedFiles);

    }
 
    public class CompressionClient
    {
        #region Private Fields  

        private static TransferSyntax destinationTransferSyntax = new TransferSyntax(
            "1.2.840.10008.1.2.4.80",
            TransferSyntax.Endian.LittleEndian,
            true,
            TransferSyntax.TransferVR.Explicit,
            true
        );
        private static IRecyclableBufferPool<byte> exactSizePool =
            RecyclableBufferPool<byte>.DirtyBuffersOfExactSize;
        private ITransferUtility transferUtility;
        private string outputPath;
        private long workingSetForProcess = 0;
        private long interationCount = 0;
        private long totalUploadTime = 0;
        private long totalCompressionTime = 0;
        private long totalFileSizeUncompressed = 0;
        private long totalFileSizeCompressed = 0;
        private Stream logsOutputFileStream;
        private Stream gcDumpOutputFileStream;
        private const int fileIOBufferSize = 64 * 1024;
        private object locker = new object();

        #endregion

        public void StartProcess(
            int threadCount,
            bool needCompression,
            int executionTimeInMinutes,
            string[] testDataCollection)
        {
            Init();
            Start(needCompression, executionTimeInMinutes, threadCount, testDataCollection);
        }

        private void Start(
            bool needCompression,
            int executionTimeInMinutes,
            int threadCount,
            string[] testDataCollection)
        {
            long totalRuns = 0;
            Stopwatch stopwatch = new Stopwatch();

            if (needCompression)
            {
                //if exection time is specified
                stopwatch.Start();
                while (stopwatch.Elapsed.TotalMinutes < executionTimeInMinutes)
                {
                    CompressAndUpload(testDataCollection, threadCount);
                    totalRuns += 1;
                }
                stopwatch.Stop();
                var output = $"\n***---Total Upload Time : {totalUploadTime}" +
                             $"\n***---Total files uploaded : {interationCount}" +
                             $"\n***---Total file size (uncompressed): {totalFileSizeUncompressed}" +
                             $"\n***---Total file size (compressed): {totalFileSizeCompressed}" +
                             $"\n***---Total Compression Time : {totalCompressionTime}" +
                             $"\n***---Total Runs: { totalRuns}";
                Console.WriteLine($"\n***--total Runs : {totalRuns}");
                byte[] info = new UTF8Encoding(true).GetBytes(output);
                logsOutputFileStream.Write(info, 0, info.Length);
            }
            else
            {
                //if exection time is specified
                stopwatch.Start();
                while (stopwatch.Elapsed.TotalMinutes < executionTimeInMinutes)
                {
                    UploadUncompressed(testDataCollection, stopwatch, threadCount);
                    totalRuns += 1;
                }
                stopwatch.Stop();
                Console.WriteLine($"\n***--Total Runs : {totalRuns}");
                var output = $"\n***---Total Upload Time : {totalUploadTime}" +
                             $"\n***---Total files uploaded : {interationCount}" +
                             $"\n***---Total file size (uncompressed): {totalFileSizeUncompressed}" +
                             $"\n***---Total file size (compressed): {totalFileSizeCompressed}" +
                             $"\n***---Total Compression Time : {totalCompressionTime}" +
                             $"\n***---Total Runs: { totalRuns}";

                byte[] info = new UTF8Encoding(true).GetBytes(output);
                logsOutputFileStream.Write(info, 0, info.Length);

            }
            Dispose();
        }


        private void CompressAndUpload(string[] testData, int parallelism)
        {
            var runDetail = $"\n**** Total parallel worker threads : {parallelism} , " +
                              $"File count {testData.Length} . ";
            var basicRuntimeInfo = ($"\n**** Basic Runtime Info {runDetail} ," + RuntimeInformationHelper.GetBasicRuntimeInfo());
            Console.WriteLine(basicRuntimeInfo);

            byte[] info = new UTF8Encoding(true).GetBytes(basicRuntimeInfo);
            logsOutputFileStream.Write(info, 0, info.Length);

            long uploadTime = 0;
            long compressionTime = 0;
            Parallel.For(0, testData.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                index =>
                {
                    var sourceFileName = Path.GetFileName(testData[index]);
                    var outputFile = Path.Combine(outputPath, sourceFileName);
                    var before = (RuntimeInformationHelper.GetRuntimeInformation());
                    var compressionTimer = new Stopwatch();
                    compressionTimer.Start();

                    InitiateCompression(testData[index], outputFile);
                    Interlocked.Increment(ref interationCount);
                    compressionTimer.Stop();
                    var after = (RuntimeInformationHelper.GetRuntimeInformation());
                    var runtimeInfo = ($"\nfile name {sourceFileName}, before :{before}, after {after}");
                    Console.WriteLine(runtimeInfo);
                    byte[] runtimeInfoByte = new UTF8Encoding(true).GetBytes(runtimeInfo);
                    lock (locker)
                    {
                        gcDumpOutputFileStream.Write(runtimeInfoByte, 0, runtimeInfoByte.Length);
                    }

                    Interlocked.Add(ref compressionTime, compressionTimer.ElapsedMilliseconds);

                    var uploadTimer = new Stopwatch();
                    uploadTimer.Start();
                    Upload(outputFile, "Compressed");
                    uploadTimer.Stop();
                    var uncompressedSize = new FileInfo(testData[index]).Length;
                    var compressedFileSize = new FileInfo(outputFile).Length;

                    Interlocked.Add(ref totalFileSizeCompressed, compressedFileSize);
                    Interlocked.Add(ref totalFileSizeUncompressed, uncompressedSize);
                    Interlocked.Add(ref uploadTime, uploadTimer.ElapsedMilliseconds);

                    var output = $"\n**** File name : {sourceFileName}, " +
                        $"size(uncompressed) : {uncompressedSize} bytes, " +
                        $"size(compressed) : {new FileInfo(outputFile).Length} bytes, " +
                        $"compressionTime : {compressionTimer.ElapsedMilliseconds} ms, " +
                        $"uploadTime : {uploadTimer.ElapsedMilliseconds} ms ";

                    byte[] info = new UTF8Encoding(true).GetBytes(output);
                    lock (locker)
                    {
                        logsOutputFileStream.Write(info, 0, info.Length);
                    }
                });
            totalUploadTime = totalUploadTime + (uploadTime / parallelism);
            totalCompressionTime = totalCompressionTime + (compressionTime / parallelism);
        }

        private void InitiateCompression(string fileName, string outputFileName)
        {
            using (var reader = new MultiframeImageReader(fileName))
            {
                var frameCount = reader.NumberOfFrames;
                using var writer = new MultiframeImageWriter(outputFileName, reader.ImageHeader, destinationTransferSyntax);
                for (int i = 0; i < frameCount; i++)
                {
                    using var rawFrameStream = reader.GetNextFrame();
                    writer.AppendFrame(rawFrameStream);
                }
                writer.Close();
            }
        }
        private void UploadUncompressed(string[] testData, Stopwatch timer, int parallelism)
        {
            Console.WriteLine($"\n**** Total parallel worker threads : {parallelism} , " +
                              $"Total file count {testData.Length} . ");
            var basicRuntimeInfo = ($"\n**** Basic Runtime Info -- {RuntimeInformationHelper.GetBasicRuntimeInfo()}");

            long uploadTime = 0;
            Parallel.For(0, testData.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                index =>
                {
                    var sourceFileName = Path.GetFileName(testData[index]);

                    var uploadTimer = new Stopwatch();
                    uploadTimer.Start();
                    Upload(testData[index], "Compressed");
                    uploadTimer.Stop();
                    var uncompressedSize = new FileInfo(testData[index]).Length;

                    Interlocked.Add(ref uploadTime, uploadTimer.ElapsedMilliseconds);
                    Interlocked.Increment(ref interationCount);
                    Interlocked.Add(ref totalFileSizeUncompressed, uncompressedSize);

                    var output = $"\n**** File name : {sourceFileName}, " +
                                 $"size(uncompressed) : {uncompressedSize} bytes " +
                                 $"uploadTime : {uploadTimer.ElapsedMilliseconds} ms ";
                    Console.WriteLine(output);
                    byte[] info = new UTF8Encoding(true).GetBytes(output);
                    lock (locker)
                    {
                        logsOutputFileStream.Write(info, 0, info.Length);
                    }
                    //Console.WriteLine($"\n**** File name : {sourceFileName}, " +
                    //    $"size(uncompressed) : {uncompressedSize} bytes, " +
                    //    $"uploadTime : {uploadTimer.ElapsedMilliseconds} ms ");
                });

            totalUploadTime = totalUploadTime + (uploadTime / parallelism);
        }

        private void Upload(string filePath, string dataType)
        {
            var fileName = Path.GetFileName(filePath);

            var transferUtilityUploadRequest = new TransferUtilityUploadRequest
            {
                BucketName = "compression-perf-bucket",
                Key = dataType + "/" + fileName,
                FilePath = filePath
            };
            try
            {
                transferUtility.Upload(transferUtilityUploadRequest);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw;
            }
        }

        private void Init()
        {
            outputPath = Path.Combine(Path.GetTempPath(), "Compressed");
            var logsOutputPath = Path.Combine(Path.GetTempPath(), "logs");
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, true);
            }
            if (Directory.Exists(logsOutputPath))
            {
                Directory.Delete(logsOutputPath, true);
            }
            Directory.CreateDirectory(outputPath);
            Directory.CreateDirectory(logsOutputPath);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            InitTransferUtility();
            var logsOutputFile = Path.Combine(logsOutputPath, DateTime.UtcNow.ToString("dd_MM_yyyy_HH_mm_ss"));

            logsOutputFileStream = new FileStream(
                logsOutputFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                fileIOBufferSize);

            var gcDumpFilePath = Path.Combine(logsOutputPath, "Dump_" + DateTime.UtcNow.ToString("dd_MM_yyyy_HH_mm_ss"));
            gcDumpOutputFileStream =
                new FileStream(
                    gcDumpFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    fileIOBufferSize);
        }

        private void InitTransferUtility()
        {
            //**---minio-------------------
            //var endPoint = "http://localhost:9000";
            //var accessKey = "minio";
            //var secretKey = "minio123";
            //var endPoint = "http://localhost:9000";
            //**---minio-------------------

            var accessKey = ConfigurationManager.AppSettings.Get("accessKey");
            var secretKey = ConfigurationManager.AppSettings.Get("secretKey");
            var sessionToken = ConfigurationManager.AppSettings.Get("sessionToken");

            var amazonConfig = new AmazonS3Config
            {
                ServiceURL = "https://s3-external-1.amazonaws.com",
                ForcePathStyle = true
            };
            var transferUtilityConfig = new TransferUtilityConfig
            {
                ConcurrentServiceRequests = 10,
                MinSizeBeforePartUpload = 16777216
            };
            var s3Client = new AmazonS3Client(accessKey, secretKey, sessionToken, amazonConfig);
            transferUtility = new TransferUtility(s3Client, transferUtilityConfig);
        }

        private void Dispose()
        {
            logsOutputFileStream.Close();
            logsOutputFileStream.Dispose();
            logsOutputFileStream = null;
            gcDumpOutputFileStream.Close();
            gcDumpOutputFileStream.Dispose();
            gcDumpOutputFileStream = null;
        }
    }


    #region Helper

    public static class Bytes
    {
        private enum Kinds
        {
            Bytes = 0,
            Kilobytes = 1,
            Megabytes = 2,
            Gigabytes = 3,
            Terabytes = 4,
        }

        public static double FromKilobytes(double kilobytes) =>
            From(kilobytes, Kinds.Kilobytes);

        public static double FromMegabytes(double megabytes) =>
            From(megabytes, Kinds.Megabytes);

        public static double FromGigabytes(double gigabytes) =>
            From(gigabytes, Kinds.Gigabytes);

        public static double FromTerabytes(double terabytes) =>
            From(terabytes, Kinds.Terabytes);

        public static double ToKilobytes(double bytes) =>
            To(bytes, Kinds.Kilobytes);

        public static double ToMegabytes(double bytes) =>
            To(bytes, Kinds.Megabytes);

        public static double ToGigabytes(double bytes) =>
            To(bytes, Kinds.Gigabytes);

        public static double ToTerabytes(double bytes) =>
            To(bytes, Kinds.Terabytes);

        private static double To(double value, Kinds kind) =>
            value / Math.Pow(1024, (int)kind);

        private static double From(double value, Kinds kind) =>
            value * Math.Pow(1024, (int)kind);
        public static string GetReadableSize(long numInBytes)
        {
            double num = numInBytes;
            string[] array = new string[5]
            {
            "B",
            "KB",
            "MB",
            "GB",
            "TB"
            };
            int num2 = 0;
            while (num >= 1024.0 && num2 < array.Length - 1)
            {
                num2++;
                num /= 1024.0;
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#}{1}", num, array[num2]);
        }
        public static string GetReadableSizeInMb(long numInBytes)
        {
            double num = numInBytes;
            string[] array = new string[5]
            {
            "B",
            "KB",
            "MB",
            "GB",
            "TB"
            };
            int num2 = 0;
            while (num >= 1024.0 && num2 < array.Length - 1)
            {
                num2++;
                num /= 1024.0;
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#}{1}", num, array[num2]);
        }
    }

    public static class RuntimeInformationHelper
    {
        public static string GetBasicRuntimeInfo()
        {
            var gcType = System.Runtime.GCSettings.IsServerGC ? "Server GC" : "Workstation GC";
            Dictionary<string, string> information = new Dictionary<string, string>();
            information.Add("UTCTime", DateTime.UtcNow.ToString("dd:MM:yyyy HH:mm:ss"));
            information.Add("Runtime", RuntimeInformation.FrameworkDescription);
            information.Add("OsDescription", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            information.Add("CPUs", Convert.ToString(Environment.ProcessorCount));
            var gcMemInfo = GC.GetGCMemoryInfo();
            information.Add("GCType", gcType);
            information.Add("GCMemoryInfoTotalAvailable", ToSize(gcMemInfo.TotalAvailableMemoryBytes));
            information.Add("GCMemoryInfoHighMemoryThreshold", ToSize(gcMemInfo.HighMemoryLoadThresholdBytes));
            return string.Join(", ", information.Select(pair => pair.Key + "=" + pair.Value)); ;
        }

        public static string GetRuntimeInformation()
        {

            var currentProcess = Process.GetCurrentProcess();
            Dictionary<string, string> information = new Dictionary<string, string>();
            information.Add("ProcessWorkingSet", ToSize(currentProcess.WorkingSet64));
            information.Add("PeakWorking Set:", ToSize(currentProcess.PeakWorkingSet64));
            var gcMemInfo = GC.GetGCMemoryInfo();
            information.Add("GCMemoryInfoHeapSize", ToSize(gcMemInfo.HeapSizeBytes));
            return string.Join(", ", information.Select(pair => pair.Key + "=" + pair.Value)); ;
            // local function to convert bytes int human readable value

        }
        private static string ToSize(long bytes)
        {
            return Bytes.GetReadableSize(bytes);
        }
    }

    #endregion

}


