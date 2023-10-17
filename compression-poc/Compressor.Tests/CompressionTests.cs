using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CharLS.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Philips.Platform.Common.DataAccess;
using Philips.Platform.Dicom;

namespace Compressor.Tests
{
    [TestClass]
    public class CompressionTests
    {
        private static string outputFile;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        [TestInitialize]
        public void TestInitialize()
        {
            //if (File.Exists(outputFile))
            //{
            //    File.Delete(outputFile);
            //}
        }

        [TestMethod]
        [DataRow("8_Bit_mono.dcm")]
        [DataRow("10_mono2.dcm")]
        [DataRow("12_mono2.dcm")]
        [DataRow("16_mono.dcm")]
        [DataRow("rgb_planar_0.dcm")]
        [DataRow("rgb_planar_1.dcm")]
        [DataRow("ybr_full_422_planar_0.dcm")]
        [DataRow("ybr_full_444_planar_0.dcm")]
        [DataRow("ybr_full_planar_1.dcm")]

        public void VerifyJPEGLSCompression(string fileName)
        {
            //Arrange
            TransferSyntax destinationTransferSyntax = new TransferSyntax(
                "1.2.840.10008.1.2.4.80",
                TransferSyntax.Endian.LittleEndian,
                true,
                TransferSyntax.TransferVR.Explicit,
                true
            );
            outputFile = Path.Combine(Path.GetTempPath(), "CompressedTestResult", Path.GetFileName(fileName));
            var testDataPath = Path.Combine("Images", fileName);

            //Act
            List<byte[]> rawFrameDataColl = new();
            using (var reader = new MultiframeImageReader(testDataPath))
            {
                var frameCount = reader.NumberOfFrames;
                var headerData = reader.ImageHeader;
                using var writer = new MultiframeImageWriter(outputFile, headerData, destinationTransferSyntax);
                Stream rawFrameStream = null;
                for (int i = 0; i < frameCount; i++)
                {
                    rawFrameStream = reader.GetNextFrame();
                    byte[] rawFrameData = new byte[rawFrameStream.Length];
                    rawFrameStream.Read(rawFrameData);
                    rawFrameDataColl.Add(rawFrameData);
                    rawFrameStream.Seek(0, SeekOrigin.Begin);
                    writer.AppendFrame(rawFrameStream);
                }
                writer.Close();
            }
            //Assert
            List<byte[]> uncompressedFrameDataColl = new();
            using (var outputReader = new MultiframeImageReader(outputFile))
            {
                var frameCountOuput = outputReader.NumberOfFrames;
                for (int i = 0; i < frameCountOuput; i++)
                {
                    var compressedFrameStream = outputReader.GetNextFrame();
                    byte[] compressedFrameBytes = new byte[compressedFrameStream.Length];
                    compressedFrameStream.Read(compressedFrameBytes);
                    using JpegLSDecoder decoder = new(compressedFrameBytes);
                    var uncompressedFrameBytes = decoder.Decode();
                    uncompressedFrameDataColl.Add(uncompressedFrameBytes);
                }
            }
            Compare(uncompressedFrameDataColl, rawFrameDataColl);
        }

        private void Compare(List<byte[]> unCompressedFrameDataColl, List<byte[]> rawFrameDataColl)
        {
            Assert.AreEqual(unCompressedFrameDataColl.Count, rawFrameDataColl.Count);
            for (int i = 0; i < unCompressedFrameDataColl.Count; i++)
            {
                Assert.IsTrue(unCompressedFrameDataColl[i].SequenceEqual(rawFrameDataColl[i]));
            }
        }
    }


}