# Meta4 Downloader

A .NET 8 console application for downloading files defined in meta4 (metalink) XML files with automatic hash verification and parallel downloads.

## Features

- ? Parse meta4/metalink XML files
- ? **Parallel file downloads** - Download multiple files simultaneously for maximum speed
- ? Automatic retry with exponential backoff on connection failures
- ? Download files from URLs with progress tracking
- ? Verify file integrity using SHA-256 hash
- ? Skip already downloaded files with valid hashes
- ? Automatic target directory creation
- ? Colored console output for status
- ? Download summary statistics

## Usage

```bash
meta4downloader <path-to-meta4-file> <target-directory> [max-concurrent-downloads]
```

### Arguments

- `<path-to-meta4-file>`: Path to the .meta4 XML file containing the download information
- `<target-directory>`: Directory where files will be downloaded (created if it doesn't exist)
- `[max-concurrent-downloads]`: (Optional) Maximum number of files to download simultaneously (default: 4)

### Examples

```bash
# Download with default 4 concurrent files
meta4downloader downloads.meta4 C:\Downloads\MyFiles

# Download with 8 concurrent files for faster speeds (if server allows)
meta4downloader downloads.meta4 C:\Downloads\MyFiles 8

# Download with 2 concurrent files for slower/unstable connections
meta4downloader downloads.meta4 C:\Downloads\MyFiles 2
```

## How It Works

The downloader uses a **simple and effective parallel approach**:
- Downloads multiple files simultaneously (default: 4 at a time)
- Each file is downloaded in a single stream (no chunking)
- Semaphore controls maximum concurrent connections
- Automatic retry (up to 3 times) with exponential backoff on failures

**Why not multi-threaded per file?**
- Simpler and more reliable
- Avoids overwhelming servers with too many connections
- Better compatibility with various servers
- Easier to control total connection count

## Error Handling & Reliability

- **Automatic retries**: Each file download automatically retries up to 3 times on network failures
- **Exponential backoff**: Retry delays increase exponentially (1s, 2s, 4s) to handle temporary server issues
- **Hash verification**: All downloads are verified with SHA-256 checksums to ensure integrity
- **Partial file cleanup**: Failed downloads automatically clean up incomplete files

## Meta4 File Format

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

## Output

The application provides:
- Real-time download progress
- Color-coded status messages:
  - ?? Green: Successfully downloaded
  - ?? Yellow: Skipped (already exists with valid hash)
  - ?? Red: Failed
- Summary statistics at the end

## Performance Tips

- **Fast connections with reliable servers**: Use 6-8 concurrent downloads
- **Moderate connections**: Default 4 concurrent downloads works well
- **Slow/unstable connections**: Use 2-3 concurrent downloads
- **Server limitations**: Some servers may rate-limit; reduce concurrent downloads if you see frequent errors

## Performance Optimizations

- **Quick file validation**: File size is checked before computing expensive hash calculations
- **Large I/O buffers**: Uses 64KB buffers for downloads and 1MB buffer for hash calculations
- **Optimized hash computation**: SHA-256 verification is ~8x faster than default implementation on large files
- **Skip existing files**: Already downloaded files with valid hashes are skipped instantly (after quick size check)
- **Parallel downloads**: Multiple files download simultaneously for maximum throughput

## Return Codes

- `0`: Success (all files downloaded or already valid)
- `1`: Error (some files failed or invalid arguments)

## Requirements

- .NET 8.0 Runtime
