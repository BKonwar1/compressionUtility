using System.Net;
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
    private static IRecyclableBufferPool<byte> exactSizePool =
        RecyclableBufferPool<byte>.DirtyBuffersOfExactSize;

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
            using CommonDicomObject frameObject = imageHeader.ShallowCopy();
            frameObject.SetBulkDataAndTransferOwnership(
                pixelDataTagWithVROB,
                framePixelData);
            CompressPixelData(frameObject, sourceTransferSyntax);
            framePixelData = frameObject.GetBulkData(pixelDataTagWithVROB);
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
            CompressPixelData(storedDicomObject, sourceTransferSyntax);
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

    private void CompressPixelData(CommonDicomObject composite, string imageHeaderTsn)
    {
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

            if (compositeObj.HasTag(DicomDictionary.DicomPixelData))
            {
                DictionaryTag newPixelDataTag = GetPixelDataTag(compositeObj);
                if (compositeObj.HasValue(newPixelDataTag))
                {
                    byte[] uncompressedData = ReadPixelData(composite, newPixelDataTag, out bool shouldFree);
                    byte[] compressedData = Compress(uncompressedData);
                    if (shouldFree)
                    {
                        FreeReadPixelData(ref uncompressedData);
                    }
                    var pixelData =
                        new RecyclableBufferMemoryStream(
                            compressedData, 0, compressedData.Length, false, false, exactSizePool);
                    compositeObj.SetBulkDataAndTransferOwnership(newPixelDataTag, pixelData);
                }
            }

            // Set the changed transfer syntax after association
            compositeObj.SetString(
                DicomDictionary.DicomTransferSyntaxUid, destinationTransferSyntax.ToString());

        }
        catch (Exception exception)
        {
            throw new TransferSyntaxConversionException(exception.Message, exception);
        }
    }

    private byte[] Compress(byte[] uncompressedData)
    {
        using var jpegEncoder = GetEncoder(uncompressedData);
        jpegEncoder.Encode(uncompressedData);
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
        var encoder = new JpegLSEncoder(frameInfo);
        //var encoder = new JpegLSEncoder();
        //encoder.Destination = new byte[uncompressedData.Length];
        //encoder.FrameInfo = frameInfo;
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

    private static byte[] ReadPixelData(
        CommonDicomObject composite, 
        DictionaryTag newPixelDataTag, 
        out bool shouldFree)
    {
        shouldFree = false;
        byte[] pixelData = null;
        if (composite is DicomObject aipObject)
        {
            var valueAsObject = aipObject.GetValue(newPixelDataTag);
            using (var stream = GetPixelDataStream(valueAsObject, out shouldFree))
            {
                pixelData = ((MemoryStream)stream).GetBuffer();
            }

            composite.Remove(DicomDictionary.DicomPixelData);
        }

        return pixelData;
    }

    private static Stream GetPixelDataStream(object valueAsObject, out bool shouldFree)
    {
        Stream memoryStream = null;
        shouldFree = false;
        if (valueAsObject.GetType() == typeof(byte[]))
        {
            byte[] bulkData = (byte[])valueAsObject;
            memoryStream = new MemoryStream(bulkData, 0, bulkData.Length, false, true);
        }
        else if (valueAsObject is Stream stream)
        {
            memoryStream = new RecyclableBufferMemoryStream((int)stream.Length);
            long origin = stream.Position;
            stream.CopyToUsingRecycledBuffer(memoryStream);
            memoryStream.Seek(origin, SeekOrigin.Begin);
            if (stream.CanSeek)
            {
                stream.Seek(origin, SeekOrigin.Begin);
            }
            shouldFree = true;
        }

        return memoryStream;
    }

    private static void FreeReadPixelData(ref byte[] buffer)
    {
        exactSizePool.Return(ref buffer);
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
