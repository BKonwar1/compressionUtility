using System.Net;
using System.Runtime.InteropServices.ComTypes;
using CharLS.Native;

using Philips.Platform.Common;
using Philips.Platform.Common.DataAccess;
using Philips.Platform.CommonUtilities.Pooling;
using Philips.Platform.Dicom;

using CommonDicomObject = Philips.Platform.Common.DicomObject;
using DicomObject = Philips.Platform.Dicom.Data.DicomObject;

namespace Compressor;

public class MultiframeImageWriter : IDisposable
{

    #region Constants

    private const int fileIOBufferSize = 64 * 1024;

    #endregion

    #region Private Fields

    private int numberOfFrames;
    private string compositeFilePath;
    private byte[] ItemDelimitationTagBytes;
    private Stream outputDataStream;
    private CommonDicomObject imageHeader;
    private DicomVR pixelDataVR = DicomVR.OB; //fixed for compressed data
    private readonly TransferSyntax destinationTransferSyntax;
    //Using a static to avoid retrieving the buffer pool for each instance
    private static IRecyclableBufferPool<byte> exactSizePool = RecyclableBufferPool<byte>.DirtyBuffersOfExactSize;

    private static DictionaryTag bytePixelDataTag = new(
        DicomDictionary.DicomPixelData.Tag,
        DicomVR.OB,
        DicomDictionary.DicomPixelData.ValueMultiplicity,
        DicomDictionary.DicomPixelData.Name,
        DicomDictionary.DicomPixelData.ImplementerId);

    private static DictionaryTag wordPixelDataTag = new(
        DicomDictionary.DicomPixelData.Tag,
        DicomVR.OW,
        DicomDictionary.DicomPixelData.ValueMultiplicity,
        DicomDictionary.DicomPixelData.Name,
        DicomDictionary.DicomPixelData.ImplementerId);

    private static readonly DictionaryTag pixelDataTagWithVROB = new(
        DicomDictionary.DicomPixelData.Tag,
        DicomVR.OB,
        DicomDictionary.DicomPixelData.ValueMultiplicity,
        DicomDictionary.DicomPixelData.Name,
        DicomDictionary.DicomPixelData.ImplementerId);

    #endregion

    #region Constructor

    public MultiframeImageWriter(
        string compositeFilePath,
        CommonDicomObject imageHeader,
        TransferSyntax destinationTransferSyntax
    )
    {
        ValidateArguments(compositeFilePath, imageHeader, destinationTransferSyntax);
        this.compositeFilePath = compositeFilePath;
        this.imageHeader = imageHeader;
        this.destinationTransferSyntax = destinationTransferSyntax;
        Init();
    }

    private void ValidateArguments(
        string compositeFilePath,
        CommonDicomObject imageHeader,
        TransferSyntax destinationTransferSyntax)
    {
        if (string.IsNullOrEmpty(compositeFilePath))
        {
            throw new ArgumentNullException("compositeFilePath");
        }
        if (imageHeader == null)
        {
            throw new ArgumentNullException("headerDicomObject");
        }
        if (destinationTransferSyntax == null)
        {
            throw new ArgumentNullException("destinationTransferSyntax");
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Appends the frame of a multi-frame image. For a multi-frame image
    /// each call will store the pixel data for the next sequential frame
    /// of the multi-frame image, whereas for a single frame image
    /// this method should be called only once.
    /// </summary>
    public void AppendFrame(Stream frameData)
    {
        if (frameData == null)
        {
            throw new ArgumentNullException("frameData");
        }
        Stream framePixelData = frameData;
        if (NeedsTransferSyntaxConversion(imageHeader, out string sourceTransferSyntax))
        {
            using CommonDicomObject imageHeaderObject = imageHeader.ShallowCopy();
            framePixelData = CompressPixelData(imageHeaderObject, frameData, sourceTransferSyntax);
        }

        SetItemDelimiter((int)framePixelData.Length);
        framePixelData.Seek(0, SeekOrigin.Begin);
        framePixelData.CopyTo(outputDataStream);
        numberOfFrames++;
    }

    /// <summary>
    /// Closes the instance
    /// </summary>
    public void Close()
    {
        //Write sequence end tag and its length
        outputDataStream.Write(BitConverter.GetBytes(0XE0DDFFFE), 0, 4);
        outputDataStream.Write(BitConverter.GetBytes(0), 0, 4);

        outputDataStream.Close();
        outputDataStream.Dispose();
        outputDataStream = null;
    }

    #endregion

    #region Private Methods

    private void Init()
    {
        outputDataStream = new FileStream(
            compositeFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            fileIOBufferSize);

        using CommonDicomObject storedDicomObject = imageHeader.ShallowCopy();
        //Apply transfer syntax conversion to update the header metadata
        if (NeedsTransferSyntaxConversion(storedDicomObject, out string sourceTransferSyntax))
        {
            CompressPixelData(storedDicomObject, null, sourceTransferSyntax);
        }
        imageHeader.Remove(DicomDictionary.DicomPixelData);

        storedDicomObject.Serialize(
            outputDataStream,
                new SerializerConfiguration
                {
                    TransferSyntax = destinationTransferSyntax.Uid
                });
        ItemDelimitationTagBytes = BitConverter.GetBytes(0XE000FFFE);
    }

    private bool NeedsTransferSyntaxConversion(CommonDicomObject storedObject, out string sourceTransferSyntax)
    {
        sourceTransferSyntax = storedObject.GetString(DicomDictionary.DicomTransferSyntaxUid);
        if (sourceTransferSyntax == null)
        {
            throw new InvalidOperationException(
                "Source DicomObject's transfer syntax can not be null.");
        }

        return !(WellKnownTransferSyntaxes.Get(sourceTransferSyntax)).Compressed;
    }

    private void SetItemDelimiter(int frameLength)
    {
        if (numberOfFrames == 0)
        {
            SetPixelDataTag();
            //Write empty BOT
            outputDataStream.Write(ItemDelimitationTagBytes, 0, 4);
            outputDataStream.Write(BitConverter.GetBytes(0), 0, 4);
        }
        if (destinationTransferSyntax.Compressed)
        {
            //Write item delimiter.
            outputDataStream.Write(ItemDelimitationTagBytes, 0, 4);
            outputDataStream.Write(
                BitConverter.GetBytes(frameLength), 0, 4);
        }
    }

    private void SetPixelDataTag()
    {
        //Write Pixeldata tag
        if (destinationTransferSyntax.ByteOrder == TransferSyntax.Endian.BigEndian)
        {
            outputDataStream.Write(BitConverter.GetBytes(
                IPAddress.HostToNetworkOrder(0x7FE00010)), 0, 4);
        }
        else
        {
            outputDataStream.Write(BitConverter.GetBytes(0x00107FE0), 0, 4);
        }
        if (destinationTransferSyntax.VRForTransfer == TransferSyntax.TransferVR.Explicit)
        {
            //Write VR
            string vr = pixelDataVR.SingleVRString;
            byte[] vrBytes = new byte[4];
            vrBytes[0] = (byte)vr[0];
            vrBytes[1] = (byte)vr[1];
            vrBytes[2] = (byte)0;
            vrBytes[3] = (byte)0;
            outputDataStream.Write(vrBytes, 0, 4);
        }

        //Write pixel data length as FFFFFFFF
        outputDataStream.Write(BitConverter.GetBytes(0xFFFFFFFF), 0, 4);
    }

    private MemoryStream CompressPixelData(CommonDicomObject composite, Stream frameDataStream, string imageHeaderTsn)
    {
        byte[] inputPixelDataBuffer = null;
        byte[] outputPixelDataBuffer = null;
        MemoryStream compressedPixelStream = null;
        try
        {
            DicomObject compositeObj = (DicomObject)composite;

            // Change endianess of composite dicom Object
            // as all compressed image are in  little endian format
            if (WellKnownTransferSyntaxes.Get(imageHeaderTsn).ByteOrder !=
                WellKnownTransferSyntaxes.ExplicitVrLittleEndian.ByteOrder)
            {

                using (var destStream = new RecyclableBufferMemoryStream())
                {
                    SerializerConfiguration configuration = new SerializerConfiguration()
                    {
                        TransferSyntax = WellKnownTransferSyntaxes.ExplicitVrLittleEndian.Uid,
                        AddExplicitSequenceLength = true
                    };
                    compositeObj.Serialize(destStream, configuration);

                    var unCompressedEleObj = (DicomObject)CommonDicomObject.CreateInstance(destStream);
                    //set the composite object transfer syntax  to ExplicitVrLittleEndian
                    //as "Combine" operation can be performed on dicom objects having same transfer syntax
                    compositeObj.TransferSyntax = WellKnownTransferSyntaxes.ExplicitVrLittleEndian;

                    compositeObj.Combine(unCompressedEleObj);
                }
            }

            if (frameDataStream != null)
            {
                var frameDataLengh = (int)frameDataStream.Length;

                //the output pixel stream buffer where compressed data would be written
                using var compressedOutputMemStream = new RecyclableBufferMemoryStream(frameDataLengh, exactSizePool);
                outputPixelDataBuffer = compressedOutputMemStream.GetBuffer();

                //the input pixel stream buffer.
                using var uncompressedInputMemStream = new RecyclableBufferMemoryStream(frameDataLengh, exactSizePool);
                inputPixelDataBuffer = uncompressedInputMemStream.GetBuffer();
                frameDataStream.Read(inputPixelDataBuffer);
                byte[] compressedData = Compress(inputPixelDataBuffer, outputPixelDataBuffer);

                compressedPixelStream =
                    new RecyclableBufferMemoryStream(
                        compressedData, 0, compressedData.Length, false, false, exactSizePool);
            }

            // Set the changed transfer syntax after association
            compositeObj.SetString(
                DicomDictionary.DicomTransferSyntaxUid, destinationTransferSyntax.ToString());

            return compressedPixelStream;

        }
        catch (Exception exception)
        {
            throw new TransferSyntaxConversionException(exception.Message, exception);
        }
        finally
        {
            if (inputPixelDataBuffer != null)
            {
                exactSizePool.Return(ref inputPixelDataBuffer);
            }
            if (outputPixelDataBuffer != null)
            {
                exactSizePool.Return(ref outputPixelDataBuffer);
            }
        }
    }

    private byte[] Compress(byte[] inputPixelData, byte[] outputBuffer)
    {
        using var jpegEncoder = GetEncoder(outputBuffer);
        jpegEncoder.Encode(inputPixelData);
        return jpegEncoder.EncodedData.ToArray();
    }

    private JpegLSEncoder GetEncoder(byte[] uncompressedData)
    {
        //****************************************
        GetImageAttributes(
            out int height,
            out int width,
            out int colorPixelDepth,
            out int components,
            out int planarConfig,
            out string photometricInterpretation);
        FrameInfo frameInfo = new(width, height, colorPixelDepth, components);

        /* //No error but reader unable to open the file
        //var encoder = new JpegLSEncoder();
        //encoder.FrameInfo = frameInfo;
        //encoder.Destination = uncompressedData;
        */

        /* // No error but pixel data is not same
        var encoder = new JpegLSEncoder(frameInfo, false);
        encoder.Destination = uncompressedData;
        encoder.InterleaveMode = GetInterleaveMode(components, photometricInterpretation, planarConfig);
        */

        /* //Method not allowed error
        var encoder = new JpegLSEncoder(frameInfo);
        encoder.Destination = uncompressedData;
        */

        var encoder = new JpegLSEncoder(frameInfo, false);
        encoder.Destination = uncompressedData;
        encoder.InterleaveMode = GetInterleaveMode(components, photometricInterpretation, planarConfig);
        return encoder;
    }

    private JpegLSInterleaveMode GetInterleaveMode(int components, string photometricInterpretation, int planarConfig)
    {
        //for Monochrome
        if (components == 1)
        {
            return JpegLSInterleaveMode.None;
        }
        // YBR Planar 0 = JpegLSInterleaveMode.Line/JpegLSInterleaveMode.Sample
        // YBR Planar 1 = JpegLSInterleaveMode.None
        if (photometricInterpretation.StartsWith("YBR"))
        {
            return planarConfig == 0 ? JpegLSInterleaveMode.Sample :
                JpegLSInterleaveMode.None;
        }
        //RGB
        return JpegLSInterleaveMode.Sample;
    }

    private void GetImageAttributes(
      out int height,
      out int width,
      out int colorPixelDepth,
      out int comp,
      out int planarConfig,
      out string photometricInterpretation)
    {
        height = imageHeader.GetUInt16(DicomDictionary.DicomRows) ?? 0;
        width = imageHeader.GetUInt16(DicomDictionary.DicomColumns) ?? 0;
        colorPixelDepth = imageHeader.GetUInt16(DicomDictionary.DicomBitsAllocated) ?? 0;
        comp = imageHeader.GetUInt16(DicomDictionary.DicomSamplesPerPixel) ?? 0;
        planarConfig = imageHeader.GetUInt16(DicomDictionary.DicomPlanarConfiguration) ?? 0;
        photometricInterpretation = imageHeader.GetString(DicomDictionary.DicomPhotometricInterpretation);
    }

    private static DictionaryTag GetPixelDataTag(CommonDicomObject composite)
    {
        var dicomPixelDataVr = composite.GetTagVR(DicomDictionary.DicomPixelData);
        DictionaryTag newPixelDataTag = null;
        if (dicomPixelDataVr != DicomVR.None)
        {
            newPixelDataTag = (dicomPixelDataVr == DicomVR.OB) ?
                bytePixelDataTag : wordPixelDataTag;
        }
        return newPixelDataTag;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);

    }
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (outputDataStream != null)
            {
                outputDataStream.Dispose();
                outputDataStream = null;
            }

            if (imageHeader != null)
            {
                imageHeader.Dispose();
                imageHeader = null;
            }
        }
    }

    #endregion

}
