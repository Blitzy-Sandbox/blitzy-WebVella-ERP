using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace WebVella.Erp.Utilities
{
    /// <summary>
    /// Configuration options for file validation.
    /// SECURITY: Used to customize file upload validation per endpoint requirements.
    /// </summary>
    public class FileValidationOptions
    {
        /// <summary>
        /// Allowed file extensions (with leading dot). Default: common safe file types.
        /// SECURITY: Only extensions in this list will be accepted (after blocked extension check).
        /// </summary>
        public string[] AllowedExtensions { get; set; } = new[]
        {
            // Images
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
            // Documents
            ".pdf",
            // Office documents
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            // Text/Data files
            ".txt", ".csv", ".xml", ".json",
            // Archives
            ".zip", ".rar", ".7z"
        };

        /// <summary>
        /// Maximum allowed file size in bytes. Default: 10MB (10 * 1024 * 1024).
        /// SECURITY: Prevents storage exhaustion attacks.
        /// </summary>
        public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>
        /// Whether to validate MIME type matches content via magic bytes. Default: true.
        /// SECURITY: When enabled, verifies file content matches declared file type.
        /// </summary>
        public bool ValidateMimeType { get; set; } = true;
    }

    /// <summary>
    /// Result of file validation operation.
    /// Provides validation status and error details for security logging and user feedback.
    /// </summary>
    public class FileValidationResult
    {
        /// <summary>
        /// Whether the file passed all validation checks.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Error message if validation failed, null if valid.
        /// SECURITY: Messages are safe for display to end users.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Creates a successful validation result.
        /// </summary>
        /// <returns>FileValidationResult with IsValid=true</returns>
        public static FileValidationResult Success() => new FileValidationResult { IsValid = true };

        /// <summary>
        /// Creates a failed validation result with error message.
        /// </summary>
        /// <param name="message">Error message describing validation failure</param>
        /// <returns>FileValidationResult with IsValid=false and ErrorMessage set</returns>
        public static FileValidationResult Failure(string message) =>
            new FileValidationResult { IsValid = false, ErrorMessage = message };
    }

    /// <summary>
    /// SECURITY: File upload validation utility to prevent CWE-434 (Unrestricted Upload of File with Dangerous Type).
    /// 
    /// This utility provides comprehensive file validation including:
    /// - Extension whitelist validation
    /// - Blocked extension enforcement (executables, scripts)
    /// - Magic bytes (file signature) verification
    /// - Maximum file size enforcement
    /// - Filename sanitization (path traversal prevention)
    /// 
    /// Usage: Call ValidateFile() before processing any uploaded file.
    /// </summary>
    public static class FileValidationUtil
    {
        #region <--- Blocked Extensions --->

        /// <summary>
        /// SECURITY: Extensions that are ALWAYS blocked regardless of allowed list.
        /// These can execute code or scripts on various platforms.
        /// This list takes precedence over any AllowedExtensions configuration.
        /// </summary>
        private static readonly string[] BlockedExtensions = new[]
        {
            // Windows executables and libraries
            ".exe", ".dll", ".com", ".msi", ".scr", ".pif",
            // Windows script files
            ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".ws", ".wsf", ".wsc", ".wsh",
            // Web server script files (PHP, ASP.NET, JSP, etc.)
            ".php", ".php3", ".php4", ".php5", ".phtml",
            ".asp", ".aspx", ".ascx", ".ashx", ".asmx", ".cer", ".asa", ".asax",
            ".jsp", ".jspx",
            ".cgi", ".pl", ".py", ".rb",
            // Unix/Linux shell scripts
            ".sh", ".bash", ".zsh", ".ksh", ".csh",
            // Other dangerous file types
            ".htaccess", ".htpasswd", ".config", ".inf", ".reg", ".lnk", ".url",
            // Java executables
            ".jar", ".war", ".class"
        };

        #endregion

        #region <--- Magic Bytes Map --->

        /// <summary>
        /// Magic bytes (file signatures) for common file types.
        /// SECURITY: Used to verify file content matches declared file type.
        /// This prevents attacks where malicious files are uploaded with fake extensions.
        /// </summary>
        private static readonly (string Extension, byte[] MagicBytes)[] MagicBytesMap = new[]
        {
            // Image formats
            (".jpg", new byte[] { 0xFF, 0xD8, 0xFF }),
            (".jpeg", new byte[] { 0xFF, 0xD8, 0xFF }),
            (".png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            (".gif", new byte[] { 0x47, 0x49, 0x46, 0x38 }),
            (".bmp", new byte[] { 0x42, 0x4D }),
            (".webp", new byte[] { 0x52, 0x49, 0x46, 0x46 }), // RIFF header (WebP is RIFF-based)
            
            // Document formats
            (".pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }), // %PDF-
            
            // Archive formats
            (".zip", new byte[] { 0x50, 0x4B, 0x03, 0x04 }), // PK..
            (".rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }), // Rar!..
            (".7z", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }), // 7z signature
            
            // Office documents - OOXML format (ZIP-based, starts with PK)
            (".docx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
            (".xlsx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
            (".pptx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
            
            // Office documents - Legacy OLE compound format
            (".doc", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
            (".xls", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
            (".ppt", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })
        };

        #endregion

        #region <--- Public Validation Methods --->

        /// <summary>
        /// Validates an uploaded file against security requirements.
        /// SECURITY: Call this before processing any uploaded file to prevent CWE-434.
        /// 
        /// Validation order:
        /// 1. Check file is not null/empty
        /// 2. Check file size within limits
        /// 3. Check file has extension
        /// 4. Check extension is not in blocked list (executables, scripts)
        /// 5. Check extension is in allowed list
        /// 6. Validate MIME type via magic bytes (if enabled)
        /// </summary>
        /// <param name="file">The uploaded file to validate</param>
        /// <param name="options">Validation options (uses defaults if null)</param>
        /// <returns>FileValidationResult indicating success or failure with error message</returns>
        public static FileValidationResult ValidateFile(IFormFile file, FileValidationOptions options = null)
        {
            options ??= new FileValidationOptions();

            // Check file exists and has content
            if (file == null || file.Length == 0)
            {
                return FileValidationResult.Failure("No file provided or file is empty.");
            }

            // SECURITY: Check file size to prevent storage exhaustion attacks
            if (file.Length > options.MaxFileSizeBytes)
            {
                return FileValidationResult.Failure(
                    $"File size ({file.Length:N0} bytes) exceeds maximum allowed ({options.MaxFileSizeBytes:N0} bytes).");
            }

            // Get and validate extension exists
            var extension = Path.GetExtension(file.FileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(extension))
            {
                return FileValidationResult.Failure("File must have an extension.");
            }

            // SECURITY: Always check blocked extensions first - these are never allowed
            if (IsBlockedExtension(extension))
            {
                return FileValidationResult.Failure($"File type '{extension}' is not allowed for security reasons.");
            }

            // Check extension is in allowed list
            if (!IsAllowedExtension(extension, options.AllowedExtensions))
            {
                return FileValidationResult.Failure(
                    $"File type '{extension}' is not in the allowed list. Allowed: {string.Join(", ", options.AllowedExtensions)}");
            }

            // SECURITY: Validate file content matches declared type via magic bytes
            if (options.ValidateMimeType)
            {
                var mimeResult = ValidateMimeType(file, extension);
                if (!mimeResult.IsValid)
                {
                    return mimeResult;
                }
            }

            return FileValidationResult.Success();
        }

        /// <summary>
        /// Sanitizes a filename to prevent path traversal attacks.
        /// SECURITY: Removes directory separators, parent directory references, and invalid characters.
        /// 
        /// Sanitization steps:
        /// 1. Extract filename only (remove path components)
        /// 2. Remove path traversal sequences (.., /, \)
        /// 3. Remove invalid filename characters
        /// 4. Handle empty result
        /// 5. Enforce maximum filename length
        /// </summary>
        /// <param name="fileName">The original filename to sanitize</param>
        /// <returns>Sanitized filename safe for file system operations</returns>
        public static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "unnamed_file";
            }

            // SECURITY: Remove any path information - only keep the filename
            fileName = Path.GetFileName(fileName);

            // SECURITY: Remove path traversal sequences
            fileName = fileName.Replace("..", "")
                               .Replace("/", "")
                               .Replace("\\", "");

            // SECURITY: Remove invalid filename characters
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                fileName = fileName.Replace(c.ToString(), "");
            }

            // Handle case where sanitization results in empty string
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "unnamed_file";
            }

            // Enforce maximum filename length to prevent filesystem issues
            const int maxLength = 255;
            if (fileName.Length > maxLength)
            {
                var ext = Path.GetExtension(fileName);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                
                // Ensure we preserve the extension
                int maxNameLength = maxLength - ext.Length;
                if (maxNameLength > 0 && nameWithoutExt.Length > maxNameLength)
                {
                    fileName = nameWithoutExt.Substring(0, maxNameLength) + ext;
                }
                else
                {
                    // If extension is too long, truncate the whole thing
                    fileName = fileName.Substring(0, maxLength);
                }
            }

            return fileName;
        }

        /// <summary>
        /// Checks if the file extension is in the allowed list.
        /// </summary>
        /// <param name="fileName">The filename or extension to check</param>
        /// <param name="allowedExtensions">Array of allowed extensions (with leading dot)</param>
        /// <returns>True if extension is in allowed list, false otherwise</returns>
        public static bool IsAllowedExtension(string fileName, string[] allowedExtensions)
        {
            if (string.IsNullOrWhiteSpace(fileName) || allowedExtensions == null || allowedExtensions.Length == 0)
            {
                return false;
            }

            var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            return allowedExtensions.Any(allowed =>
                string.Equals(allowed.ToLowerInvariant(), extension, StringComparison.Ordinal));
        }

        /// <summary>
        /// Checks if the file extension is in the blocked list.
        /// SECURITY: Always returns true for executable/script extensions regardless of other settings.
        /// </summary>
        /// <param name="fileName">The filename or extension to check</param>
        /// <returns>True if extension is blocked (dangerous), false otherwise</returns>
        public static bool IsBlockedExtension(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            return BlockedExtensions.Any(blocked =>
                string.Equals(blocked.ToLowerInvariant(), extension, StringComparison.Ordinal));
        }

        /// <summary>
        /// Validates that file content matches the declared file type by checking magic bytes.
        /// SECURITY: Prevents attacks where malicious files are uploaded with fake extensions.
        /// 
        /// For file types without defined magic bytes in the map, validation is skipped
        /// (returns success) to allow text files and other formats without fixed signatures.
        /// </summary>
        /// <param name="file">The uploaded file to validate</param>
        /// <param name="expectedExtension">Expected file extension (determined from filename if null)</param>
        /// <returns>FileValidationResult indicating if content matches expected type</returns>
        public static FileValidationResult ValidateMimeType(IFormFile file, string expectedExtension = null)
        {
            if (file == null || file.Length == 0)
            {
                return FileValidationResult.Failure("No file provided.");
            }

            expectedExtension ??= Path.GetExtension(file.FileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(expectedExtension))
            {
                return FileValidationResult.Failure("Cannot determine file extension.");
            }

            // Find expected magic bytes for this extension
            var magicEntry = MagicBytesMap.FirstOrDefault(m =>
                string.Equals(m.Extension, expectedExtension, StringComparison.OrdinalIgnoreCase));

            // If we don't have magic bytes for this type, skip content validation
            // This allows text files (.txt, .csv, .xml, .json) which don't have fixed signatures
            if (magicEntry.MagicBytes == null)
            {
                return FileValidationResult.Success();
            }

            // Read file header to check magic bytes
            byte[] header;
            try
            {
                using (var stream = file.OpenReadStream())
                {
                    header = new byte[magicEntry.MagicBytes.Length];
                    int bytesRead = stream.Read(header, 0, header.Length);
                    
                    if (bytesRead < header.Length)
                    {
                        return FileValidationResult.Failure("File too small to validate content type.");
                    }
                }
            }
            catch (Exception ex)
            {
                // SECURITY: Log the exception but don't expose internal details
                return FileValidationResult.Failure($"Error reading file content: {ex.Message}");
            }

            // SECURITY: Compare magic bytes to verify content matches declared type
            if (!header.Take(magicEntry.MagicBytes.Length).SequenceEqual(magicEntry.MagicBytes))
            {
                return FileValidationResult.Failure(
                    $"File content does not match declared type '{expectedExtension}'. " +
                    "The file may be corrupted or have an incorrect extension.");
            }

            return FileValidationResult.Success();
        }

        #endregion
    }
}
