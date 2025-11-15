# Meta4 Downloader

A .NET 8 console application for downloading files from meta4 (metalink) XML files with automatic hash verification and parallel downloads.

## Features

- ? Parse meta4/metalink XML files
- ? Parallel file downloads for maximum speed
- ? Automatic retry with exponential backoff on connection failures
- ? SHA-256 hash verification for file integrity
- ? Smart file validation (skips already downloaded files)
- ? Progress tracking with visual progress bars
- ? Colored console output for easy status monitoring
- ? Automatic target directory creation

## Usage

```bash
meta4downloader <path-to-meta4-file> <target-directory> [max-concurrent-downloads]
```

### Arguments

| Argument | Required | Description | Default |
|----------|----------|-------------|---------|
| `path-to-meta4-file` | Yes | Path to the .meta4 XML file | - |
| `target-directory` | Yes | Directory where files will be downloaded | - |
| `max-concurrent-downloads` | No | Maximum number of files to download simultaneously | 4 |

### Examples

```bash
# Download with default settings (4 concurrent files)
meta4downloader downloads.meta4 C:\Downloads\MyFiles

# Download with 8 concurrent files for faster speeds
meta4downloader downloads.meta4 C:\Downloads\MyFiles 8

# Download with 2 concurrent files for slower connections
meta4downloader downloads.meta4 C:\Downloads\MyFiles 2
```

## Supported File Format

The application supports the standard metalink XML format (urn:ietf:params:xml:ns:metalink):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<metalink xmlns="urn:ietf:params:xml:ns:metalink">
    <file name="example.jp2">
        <size>124468414</size>
   <hash type="sha-256">3403f610f6d23446cf9a7d2e9fb853ebad5516c68d401dfc5a9dc48b0e1f538a</hash>
<url>https://example.com/data/example.jp2</url>
    </file>
</metalink>
```

## Console Output

The application provides real-time feedback with:

- **Progress indicators**: Visual progress bars showing download percentage
- **Status updates**: Color-coded messages for each file
  - ?? Green checkmark: Successfully downloaded
  - ?? Yellow circle: Skipped (already exists with valid hash)
  - ?? Red X: Failed download
- **Overall progress**: Tracks completion across all files
- **Summary statistics**: Final report with download counts and total bytes

### Example Output

```
Starting downloads...

? file1.jp2[????????????????????] 20.0% (25.0 MB/124.5 MB)
? file2.j2w  Complete (38 B)
Overall: [??????????????????????????] 14.3% (1/7 files, 38 B downloaded)

? file3.j2w  Skipped (valid)
Overall: [??????????????????????????] 28.6% (2/7 files, 38 B downloaded)

???????????????????????????????????????????????????????????
Download Summary:
  Total files:       7
  Downloaded:        5
Skipped (valid):   2
  Failed:            0
  Total downloaded:  650.5 MB
???????????????????????????????????????????????????????????
```

## Performance Tips

- **Fast connections**: Use 6-8 concurrent downloads
- **Moderate connections**: Default 4 concurrent downloads works well
- **Slow/unstable connections**: Use 2-3 concurrent downloads
- **Server rate limiting**: Reduce concurrent downloads if you encounter frequent errors

## Error Handling

- **Automatic retries**: Failed downloads retry up to 3 times with increasing delays (1s, 2s, 4s)
- **Hash verification**: All files are verified against SHA-256 checksums
- **Partial file cleanup**: Incomplete downloads are automatically removed
- **Smart validation**: Files are checked by size first, then hash (faster validation)

## Return Codes

| Code | Meaning |
|------|---------|
| `0` | Success - all files downloaded or already valid |
| `1` | Error - some files failed or invalid arguments |

## Requirements

- .NET 8.0 Runtime or later

## Installation

Download the latest release or build from source:

```bash
dotnet build -c Release
```

The executable will be in `bin/Release/net8.0/`
