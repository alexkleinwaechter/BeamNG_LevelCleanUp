# Meta4 Downloader

A .NET 8 console application for downloading files defined in meta4 (metalink) XML files with automatic hash verification.

## Features

- ? Parse meta4/metalink XML files
- ? Download files from URLs with progress tracking
- ? Verify file integrity using SHA-256 hash
- ? Parallel downloads (up to 4 concurrent downloads)
- ? Skip already downloaded files with valid hashes
- ? Automatic target directory creation
- ? Colored console output for status
- ? Download summary statistics

## Usage

```bash
meta4downloader <path-to-meta4-file> <target-directory>
```

### Arguments

- `<path-to-meta4-file>`: Path to the .meta4 XML file containing the download information
- `<target-directory>`: Directory where files will be downloaded (created if it doesn't exist)

### Example

```bash
meta4downloader downloads.meta4 C:\Downloads\MyFiles
```

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

## Return Codes

- `0`: Success (all files downloaded or already valid)
- `1`: Error (some files failed or invalid arguments)

## Requirements

- .NET 8.0 Runtime
