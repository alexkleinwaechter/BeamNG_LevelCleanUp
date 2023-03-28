using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Utils
{
    public static class StringUtils
    {
        enum ParserState
        {
            PossibleDriveLetter,
            PossibleDriveLetterSeparator,
            Path
        }

        public static string SanitizeFileName(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }
            StringBuilder output = new StringBuilder(input.Length);
            ParserState state = ParserState.PossibleDriveLetter;
            foreach (char current in input)
            {
                if (((current >= 'a') && (current <= 'z')) || ((current >= 'A') && (current <= 'Z')))
                {
                    output.Append(current);
                    if (state == ParserState.PossibleDriveLetter)
                    {
                        state = ParserState.PossibleDriveLetterSeparator;
                    }
                    else
                    {
                        state = ParserState.Path;
                    }
                }
                else if ((current == Path.DirectorySeparatorChar) ||
                    (current == Path.AltDirectorySeparatorChar) ||
                    ((current == ':') && (state == ParserState.PossibleDriveLetterSeparator)) ||
                    !Path.GetInvalidFileNameChars().Contains(current))
                {

                    output.Append(current);
                    state = ParserState.Path;
                }
                else
                {
                    output.Append('_');
                    state = ParserState.Path;
                }
            }
            return output.ToString().Replace(" ", "");
        }
    }
}
