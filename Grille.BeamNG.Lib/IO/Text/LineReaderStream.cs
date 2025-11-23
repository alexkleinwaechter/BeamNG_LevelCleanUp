using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Text;
internal class LineReaderStream : Stream
{
    public string Text { get; }

    public LineReaderStream(string text)
    {
        Text = text;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    long offset;
    long length;
    public override long Length => length;

    long position;
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public void SetSection(int start, int length)
    {

    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return 0;// BaseStream.Read(buffer, offset + Offset, count);
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public static IEnumerable<Stream> GetLinePositions(string input)
    {
        var stream = new LineReaderStream(input);
        
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        int start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\n' || input[i] == '\r')
            {
                int length = i - start;
                stream.SetSection(start, length);
                yield return stream;

                // Handle CRLF (\r\n)
                if (input[i] == '\r' && i + 1 < input.Length && input[i + 1] == '\n')
                    i++;

                start = i + 1;
            }
        }

        // Yield the last line if there's no trailing newline
        if (start < input.Length)
        {
            stream.SetSection(start, input.Length-start);
            yield return stream;
        }
    }
}
