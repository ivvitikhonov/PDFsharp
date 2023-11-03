// PDFsharp - A .NET library for processing PDF
// See the LICENSE file in the solution root for more information.

using PdfSharp.Drawing;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.Internal;
using PdfSharp.Pdf.IO;
using System.Diagnostics.CodeAnalysis;
#if WPF
using System.IO;
#endif

namespace PdfSharp.Pdf.Signatures
{
    /// <summary>
    /// PdfDocument signature handler.
    /// Attaches a PKCS#7 signature digest to PdfDocument.
    /// Digest algorithm will be either SHA256/SHA512 depending on PdfDocument.Version.
    /// </summary>
    public class PdfSignatureHandler
    {
        private PdfString? signatureFieldContentsPdfString;
        private PdfArrayWithPadding? signatureFieldByteRangePdfArray;

        /// <summary>
        /// Cache signature length (bytes) for each PDF version since digest length depends on digest algorithm that depends on PDF version.
        /// </summary>
        private static readonly Dictionary<int, int> knownSignatureLengthInBytesByPdfVersion = new();

        private const int byteRangePaddingLength = 36; // place big enough required to replace [0 0 0 0] with the correct value

        public PdfDocument? Document { get; private set; }
        public PdfSignatureOptions Options { get; private set; }
        private ISigner signer { get; set; }

        public void AttachToDocument(PdfDocument documentToSign)
        {
            this.Document = documentToSign;
            this.Document.BeforeSave += AddSignatureComponents;
            this.Document.AfterSave += ComputeSignatureAndRange;

            // estimate signature length by computing signature for a fake byte[]
            if (!knownSignatureLengthInBytesByPdfVersion.ContainsKey(documentToSign.Version))
                knownSignatureLengthInBytesByPdfVersion[documentToSign.Version] = signer.GetSignedCms(new MemoryStream(new byte[] { 0 }), documentToSign.Version).Length;
        }

        public PdfSignatureHandler(ISigner signer, PdfSignatureOptions options)
        {
            this.signer = signer;
            this.Options = options;
        }

        private void AddSignatureComponents(object? sender, EventArgs e)
        {
            // Document cannot be null because this method is attached only after setting the field

            var fakeSignature = Enumerable.Repeat(
                (byte)0x20, // actual value does not matter
                knownSignatureLengthInBytesByPdfVersion[Document!.Version]).ToArray();

            var fakeSignatureAsRawString = PdfEncoders.RawEncoding.GetString(fakeSignature, 0, fakeSignature.Length);
            signatureFieldContentsPdfString = new PdfString(fakeSignatureAsRawString, PdfStringFlags.HexLiteral);
            signatureFieldByteRangePdfArray = new PdfArrayWithPadding(Document, byteRangePaddingLength,
                new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));

            var signatureDictionary = AddSignatureDictionary(signatureFieldContentsPdfString, signatureFieldByteRangePdfArray);
            var signatureField = AddSignatureField(signatureDictionary);

            var annotations = Document.Pages[0].Elements.GetArray(PdfPage.Keys.Annots);
            if (annotations == null)
                Document.Pages[0].Elements.Add(PdfPage.Keys.Annots, new PdfArray(Document, signatureField));
            else
                annotations.Elements.Add(signatureField);

            FillAccroForm(Document, signatureField);
        }

        private PdfDictionary AddSignatureDictionary(PdfString contents, PdfArray byteRange)
        {
            // Document cannot be null because this method is attached only after setting the field
            PdfDictionary signatureDic = new PdfDictionary(Document!);

            signatureDic.Elements.Add(PdfSignatureField.Keys.Type, new PdfName("/Sig"));
            signatureDic.Elements.Add(PdfSignatureField.Keys.Filter, new PdfName("/Adobe.PPKLite"));
            signatureDic.Elements.Add(PdfSignatureField.Keys.SubFilter, new PdfName("/adbe.pkcs7.detached"));
            signatureDic.Elements.Add(PdfSignatureField.Keys.M, new PdfDate(DateTime.Now));

            signatureDic.Elements.Add(PdfSignatureField.Keys.Contents, contents);
            signatureDic.Elements.Add(PdfSignatureField.Keys.ByteRange, byteRange);
            if (Options.Reason != null)
                signatureDic.Elements.Add(PdfSignatureField.Keys.Reason, new PdfString(Options.Reason));
            if (Options.Location != null)
                signatureDic.Elements.Add(PdfSignatureField.Keys.Location, new PdfString(Options.Location));

            Document!.Internals.AddObject(signatureDic);

            return signatureDic;
        }

        private PdfSignatureField AddSignatureField(PdfDictionary signatureDic)
        {
            // Document cannot be null because this method is attached only after setting the field
            var signatureField = new PdfSignatureField(Document!);

            signatureField.Elements.Add(PdfSignatureField.Keys.V, signatureDic);

            // annotation keys
            signatureField.Elements.Add(PdfSignatureField.Keys.FT, new PdfName("/Sig"));
            signatureField.Elements.Add(PdfSignatureField.Keys.T, new PdfString("Signature1")); // TODO? if already exists, will it cause error? implement a name choser if yes
            signatureField.Elements.Add(PdfSignatureField.Keys.Ff, new PdfInteger(132));
            signatureField.Elements.Add(PdfSignatureField.Keys.DR, new PdfDictionary());
            signatureField.Elements.Add(PdfSignatureField.Keys.Type, new PdfName("/Annot"));
            signatureField.Elements.Add("/Subtype", new PdfName("/Widget"));
            signatureField.Elements.Add("/P", Document!.Pages[0]);

            signatureField.Elements.Add("/Rect", new PdfRectangle(Options.Rectangle));

            signatureField.CustomAppearanceHandler = Options.AppearanceHandler ?? new DefaultSignatureAppearanceHandler()
            {
                Location = Options.Location,
                Reason = Options.Reason,
                Signer = signer.GetName()
            };

            // TODO: for some reason, PdfSignatureField.PrepareForSave() is not triggered automatically so let's call it manually from here, but it would be better to be called automatically
            signatureField.PrepareForSave();
            
            Document.Internals.AddObject(signatureField);

            return signatureField;
        }

        private static void FillAccroForm(PdfDocument document, PdfSignatureField signatureField)
        {
            var catalog = document.Catalog;

            if (catalog.Elements.GetObject(PdfCatalog.Keys.AcroForm) == null)
                catalog.Elements.Add(PdfCatalog.Keys.AcroForm, new PdfAcroForm(document));

            if (!catalog.AcroForm.Elements.ContainsKey(PdfAcroForm.Keys.SigFlags))
                catalog.AcroForm.Elements.Add(PdfAcroForm.Keys.SigFlags, new PdfInteger(3));
            else
            {
                var sigFlagVersion = catalog.AcroForm.Elements.GetInteger(PdfAcroForm.Keys.SigFlags);
                if (sigFlagVersion < 3)
                    catalog.AcroForm.Elements.SetInteger(PdfAcroForm.Keys.SigFlags, 3);
            }

            if (catalog.AcroForm.Elements.GetValue(PdfAcroForm.Keys.Fields) == null)
                catalog.AcroForm.Elements.SetValue(PdfAcroForm.Keys.Fields, new PdfAcroField.PdfAcroFieldCollection(new PdfArray()));
            catalog.AcroForm.Fields.Elements.Add(signatureField);
        }

        private void ComputeSignatureAndRange(object? sender, PdfDocumentEventArgs e)
        {
            var writer = e.Writer;

            // DEBUG mode makes the writer Verbose and will introduce 1 extra space between entries key and value
            // if Verbose, a space is added between entry key and entry value
            var isVerbose = writer.Layout == PdfWriterLayout.Verbose;
            var verboseExtraSpaceSeparatorLength = isVerbose ? 1 : 0;

            var (rangedStreamToSign, byteRangeArray) = GetRangeToSignAndByteRangeArray(writer.Stream, verboseExtraSpaceSeparatorLength);

            WriteByteRange(writer, byteRangeArray);
            WriteDocumentDigest(writer, verboseExtraSpaceSeparatorLength, rangedStreamToSign);
        }

        /// <summary>
        /// Get the bytes ranges to sign.
        /// As recommended in PDF specs, whole document will be signed, except for the hexadecimal signature token value in the /Contents entry.
        /// Example: '/Contents <aaaaa111111>' => '<aaaaa111111>' will be excluded from the bytes to sign.
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="verboseExtraSpaceSeparatorLength"></param>
        /// <returns></returns>
        private (ReadOnlyRangedStream rangedStream, PdfArray byteRangeArray) GetRangeToSignAndByteRangeArray(Stream stream, int verboseExtraSpaceSeparatorLength)
        {
            if (signatureFieldContentsPdfString is null)
                throw new InvalidOperationException("Signature field field content was not initialized as it should have been during BeforeSave");

            int firstRangeOffset = 0,
                firstRangeLength = signatureFieldContentsPdfString.PositionStart + verboseExtraSpaceSeparatorLength,
                secondRangeOffset = signatureFieldContentsPdfString.PositionEnd,
                secondRangeLength = (int)stream.Length - signatureFieldContentsPdfString.PositionEnd;

            var byteRangeArray = new PdfArray();
            byteRangeArray.Elements.Add(new PdfInteger(firstRangeOffset));
            byteRangeArray.Elements.Add(new PdfInteger(firstRangeLength));
            byteRangeArray.Elements.Add(new PdfInteger(secondRangeOffset));
            byteRangeArray.Elements.Add(new PdfInteger(secondRangeLength));

            var rangedStream = new ReadOnlyRangedStream(stream, new List<ReadOnlyRangedStream.Range>()
            {
                new ReadOnlyRangedStream.Range(firstRangeOffset, firstRangeLength),
                new ReadOnlyRangedStream.Range(secondRangeOffset, secondRangeLength)
            });

            return (rangedStream, byteRangeArray);
        }

        private void WriteByteRange(PdfWriter writer, PdfArray byteRangeArray)
        {
            if (signatureFieldByteRangePdfArray is null)
                throw new InvalidOperationException("Signature field byte range was not initialized as it should have been during BeforeSave");

            // writing actual ByteRange in place of the placeholder
            writer.Stream.Position = signatureFieldByteRangePdfArray.PositionStart;
            byteRangeArray.WriteObject(writer);
        }

        private void WriteDocumentDigest(PdfWriter writer, Int32 verboseExtraSpaceSeparatorLength, ReadOnlyRangedStream rangedStreamToSign)
        {
            if (signatureFieldContentsPdfString is null)
                throw new InvalidOperationException("Signature field field content was not initialized as it should have been during BeforeSave");

            // digestString is orphan, so it will not write the space delimiter: need to begin write 1 byte further if Verbose
            PdfString digestString = ComputeDocumentDigest(rangedStreamToSign);

            // writing actual digest in place of the placeholder
            writer.Stream.Position = signatureFieldContentsPdfString.PositionStart + verboseExtraSpaceSeparatorLength;
            digestString.WriteObject(writer);
        }

        private PdfString ComputeDocumentDigest(ReadOnlyRangedStream rangedStreamToSign)
        {
            // Document cannot be null because this method is called by other methods attached only after setting the field
            var signature = signer.GetSignedCms(rangedStreamToSign, Document!.Version);

            if (signature.Length != knownSignatureLengthInBytesByPdfVersion[Document.Version])
                throw new Exception("The digest length is different that the approximation made.");

            var signatureAsRawString = PdfEncoders.RawEncoding.GetString(signature, 0, signature.Length);
            var pdfString = new PdfString(signatureAsRawString, PdfStringFlags.HexLiteral); // has to be a hex string
            return pdfString;
        }
    }
}
