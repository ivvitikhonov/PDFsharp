// PDFsharp - A .NET library for processing PDF
// See the LICENSE file in the solution root for more information.

#if WPF
using System.IO;
#endif

namespace PdfSharp.Pdf.Signatures
{
    /// <summary>
    /// This stream wraps an existing stream and returns sections of it (ranges)
    /// as if it were a single, existing stream.
    /// </summary>
    internal class ReadOnlyRangedStream : Stream
    {
        private Range[] ranges;

        public class Range
        {

            public Range(long offset, long length)
            {
                this.Offset = offset;
                this.Length = length;
            }

            /// <summary>
            /// Start position of the range in the underlying stream
            /// </summary>
            public long Offset { get; set; }

            /// <summary>
            /// Length of the range
            /// </summary>
            public long Length { get; set; }

            public long EndPosition => Offset + Length;
        }

        private Stream stream { get; set; }


        public ReadOnlyRangedStream(Stream originalStream, List<Range> ranges)
        {
            this.stream = originalStream;

            if (ranges.Count == 0)
                throw new InvalidOperationException("ReadOnlyRangedStream requires at least one range");

            long previousRangeEndPosition = 0;
            this.ranges = ranges.OrderBy(item => item.Offset).ToArray();
            foreach (var range in ranges)
            {
                if (range.Offset < previousRangeEndPosition)
                    throw new Exception("Ranges are overlapping");
                previousRangeEndPosition = range.EndPosition;
            }
        }


        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;

        public override long Length => ranges.Sum(item => item.Length);


        private IEnumerable<Range> GetPreviousRanges(long position)
        {
            return ranges.Where(item => item.Offset + item.Length < position);
        }

        private Range? GetCurrentRange(long position)
        {
            return ranges.FirstOrDefault(item => item.Offset <= position && item.Offset + item.Length > position);
        }

        private Range GetNextRange()
        {
            return ranges.First(item => item.Offset > stream.Position);
        }

        public override long Position
        {
            get
            {
                var currentRange = GetCurrentRange(stream.Position);
                if (currentRange is null)
                    throw new InvalidOperationException("Underlying stream position is outside defined ranges");

                return GetPreviousRanges(stream.Position).Sum(item => item.Length) + stream.Position - currentRange.Offset;
            }

            set
            {
                Range currentRange = ranges[0];
                long maxPosition = currentRange.Length;
                foreach (var range in ranges.Skip(1))
                {
                    if (maxPosition > value)
                        break;
                    currentRange = range;
                    maxPosition += range.Length;
                }

                long positionInCurrentRange = value - (maxPosition - currentRange.Length);
                stream.Position = currentRange.Offset + positionInCurrentRange;
            }
        }


        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = stream.Length;
            int retVal = 0;
            for (int i = 0; i < count; i++)
            {
                if (stream.Position == length)
                {
                    break;
                }

                PerformSkipIfNeeded();
                retVal += stream.Read(buffer, offset++, 1);
            }

            return retVal;
        }


        private void PerformSkipIfNeeded()
        {
            var currentRange = GetCurrentRange(stream.Position);

            if (currentRange == null)
                stream.Position = GetNextRange().Offset;
        }


        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new NotSupportedException("Seeking with an unsupported SeekOrigin: " + origin)
            };
            return Position;
        }

        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
        public override void Flush() => throw new NotImplementedException();
    }
}
