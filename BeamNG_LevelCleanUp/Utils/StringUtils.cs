using System.Text;
using System.Text.RegularExpressions;

namespace BeamNG_LevelCleanUp.Utils;

public static class StringUtils
{
    public static string SanitizeFileName(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        var output = new StringBuilder(input.Length);
        var state = ParserState.PossibleDriveLetter;
        
        foreach (var current in input)
        {
            // Convert umlauts to their ASCII equivalents
            var converted = ConvertUmlaut(current);
            
            // If conversion resulted in multiple characters (e.g., ä -> ae)
            if (converted.Length > 1)
            {
                foreach (var c in converted)
                {
                    output.Append(char.ToLowerInvariant(c));
                }
                state = ParserState.Path;
                continue;
            }
            
            var charToProcess = converted[0];
            
            if ((charToProcess >= 'a' && charToProcess <= 'z') || (charToProcess >= 'A' && charToProcess <= 'Z'))
            {
                output.Append(char.ToLowerInvariant(charToProcess));
                if (state == ParserState.PossibleDriveLetter)
                    state = ParserState.PossibleDriveLetterSeparator;
                else
                    state = ParserState.Path;
            }
            else if (charToProcess >= '0' && charToProcess <= '9')
            {
                // Allow numbers
                output.Append(charToProcess);
                state = ParserState.Path;
            }
            else if (charToProcess == Path.DirectorySeparatorChar ||
                     charToProcess == Path.AltDirectorySeparatorChar ||
                     (charToProcess == ':' && state == ParserState.PossibleDriveLetterSeparator))
            {
                // Allow path separators and drive letter separator
                output.Append(charToProcess);
                state = ParserState.Path;
            }
            else if (charToProcess == '_' || charToProcess == '-')
            {
                // Allow underscore and hyphen
                output.Append(charToProcess);
                state = ParserState.Path;
            }
            else if (!Path.GetInvalidFileNameChars().Contains(charToProcess) && charToProcess != ' ')
            {
                // Skip spaces and other special characters (don't replace with underscore)
                // This removes special characters instead of replacing them
                state = ParserState.Path;
            }
            // Else: skip the character (removes special chars)
        }

        return output.ToString();
    }

    /// <summary>
    /// Converts German umlauts and other special characters to their ASCII equivalents
    /// </summary>
    private static string ConvertUmlaut(char c)
    {
        return c switch
        {
            // German umlauts
            'ä' or 'Ä' => "ae",
            'ö' or 'Ö' => "oe",
            'ü' or 'Ü' => "ue",
            'ß' => "ss",
            
            // French accents
            'à' or 'À' => "a",
            'â' or 'Â' => "a",
            'é' or 'É' => "e",
            'è' or 'È' => "e",
            'ê' or 'Ê' => "e",
            'ë' or 'Ë' => "e",
            'î' or 'Î' => "i",
            'ï' or 'Ï' => "i",
            'ô' or 'Ô' => "o",
            'ù' or 'Ù' => "u",
            'û' or 'Û' => "u",
            'ÿ' or 'Ÿ' => "y",
            'ç' or 'Ç' => "c",
            
            // Spanish
            'á' or 'Á' => "a",
            'í' or 'Í' => "i",
            'ó' or 'Ó' => "o",
            'ú' or 'Ú' => "u",
            'ñ' or 'Ñ' => "n",
            
            // Scandinavian
            'å' or 'Å' => "aa",
            'æ' or 'Æ' => "ae",
            'ø' or 'Ø' => "oe",
            
            // Other common characters
            'œ' or 'Œ' => "oe",
            
            // Default: return as single-character string
            _ => c.ToString()
        };
    }

    public static string JBeamToJSON(string jbeamtext)
    {
        jbeamtext = Regex.Replace(jbeamtext, @"//.*\r\n", delegate { return ""; });

        //replace /* ... */ with ""
        jbeamtext = Regex.Replace(jbeamtext, @"/\*[^\*]*\*/", "");

        //jbeam allows for newlines to be in place of commas. Who thought that was a good idea?
        //Replace any letter or number, or closing array (]) or dictionary (}), with 0 or more spaces between it (\s*?) and the newline (\n\r) with ITSELF, plus a comma, plus a newline
        jbeamtext = Regex.Replace(jbeamtext, @"[a-zA-Z0-9\]\}]\s*?\r\n", delegate(Match match)
        {
            var text = match.ToString();
            return text[0] + ",\r\n";
        });

        //this fixes the case where we have 2 numbers next to eachother { ([0-9])[\s]+([\-0-9]) } outside of a quote { (""[^""]*?""|\Z) } and separated by AT LEAST ONE SPACE { [\s]+ }
        jbeamtext = Regex.Replace(jbeamtext, @"((?:[^""]*?([0-9])[\s]+([\-0-9])[^""]*?)*)(""[^""]*?""|\Z)",
            delegate(Match match)
            {
                //non-quoted portion of the cut
                var nonquote = match.Groups[1];
                //quoted portion
                var quote = match.Groups[4];

                //fix the first pairs (2,3 4,5)
                var oddreplacements = Regex.Replace(nonquote.Value, @"([0-9])[\s]+([\-0-9])", "$1,$2");
                //fix the second pairs (2,3,4,5)
                var evenreplacements = Regex.Replace(oddreplacements, @"([0-9])[\s]+([\-0-9])", "$1,$2");
                //add this fix back on to the entire string
                return evenreplacements + quote.Value;
            });

        //fix ,] and ,}
        jbeamtext = Regex.Replace(jbeamtext, @",[\s\r\n]*?\]", "]");
        jbeamtext = Regex.Replace(jbeamtext, @",[\s\r\n]*?\}", "}");

        //lazy hack, remove the trailing comma after the last brace if there is one
        jbeamtext = Regex.Replace(jbeamtext, @",[\r\n\s]*\Z", "");


        //fix errors made by the authors...

        //these fix the errors where the author did something silly like }"
        jbeamtext = Regex.Replace(jbeamtext, @"\}[\r\n\s]*?" + "\"", "},\"");
        jbeamtext = Regex.Replace(jbeamtext, @"\][\r\n\s]*?" + "\"", "],\"");

        //this fixes the errors where we have something like "foo" "bar" in an array, where it should be "foo", "bar"
        jbeamtext = Regex.Replace(jbeamtext, @"((?<!\s*:\s*)"")[\s]*""", "$1,\"");

        return jbeamtext;
    }

    private enum ParserState
    {
        PossibleDriveLetter,
        PossibleDriveLetterSeparator,
        Path
    }
}