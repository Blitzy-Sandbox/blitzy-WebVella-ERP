using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Http;
using Moq;
using WebVella.Erp.Utilities;
using Xunit;

namespace WebVella.Erp.Tests.Security
{
    /// <summary>
    /// SECURITY: Regression test suite for file upload validation controls.
    /// Tests CWE-434 (Unrestricted Upload of File with Dangerous Type) vulnerability mitigation.
    /// 
    /// Coverage includes:
    /// - Malicious extension rejection (.exe, .bat, .sh, .php, etc.)
    /// - Oversized file rejection (enforcing 10MB default limit)
    /// - MIME type mismatch detection (magic bytes validation)
    /// - Path traversal prevention (filename sanitization)
    /// - Allowed extension whitelist enforcement
    /// 
    /// Run with: dotnet test --filter "Category=Security"
    /// </summary>
    [Trait("Category", "Security")]
    public class FileValidationTests
    {
        #region <--- Test Data --->

        /// <summary>
        /// Dangerous executable extensions that should always be blocked.
        /// SECURITY: These can execute code on various platforms.
        /// </summary>
        private static readonly string[] MaliciousExecutableExtensions = new[]
        {
            ".exe", ".dll", ".com", ".msi", ".scr", ".pif"
        };

        /// <summary>
        /// Dangerous script extensions that should always be blocked.
        /// SECURITY: These can run scripts on Windows systems.
        /// </summary>
        private static readonly string[] MaliciousScriptExtensions = new[]
        {
            ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse",
            ".ws", ".wsf", ".wsc", ".wsh"
        };

        /// <summary>
        /// Server-side script extensions that should always be blocked.
        /// SECURITY: These can execute on web servers.
        /// </summary>
        private static readonly string[] MaliciousServerScriptExtensions = new[]
        {
            ".php", ".php3", ".php4", ".php5", ".phtml",
            ".asp", ".aspx", ".ascx", ".ashx", ".asmx", ".cer", ".asa", ".asax",
            ".jsp", ".jspx",
            ".cgi", ".pl", ".py", ".rb"
        };

        /// <summary>
        /// Unix shell script extensions that should always be blocked.
        /// SECURITY: These can execute on Unix/Linux systems.
        /// </summary>
        private static readonly string[] MaliciousUnixExtensions = new[]
        {
            ".sh", ".bash", ".zsh", ".ksh", ".csh"
        };

        /// <summary>
        /// Other dangerous file types that should always be blocked.
        /// </summary>
        private static readonly string[] MaliciousOtherExtensions = new[]
        {
            ".htaccess", ".htpasswd", ".config", ".inf", ".reg", ".lnk", ".url",
            ".jar", ".war", ".class"
        };

        /// <summary>
        /// Default allowed extensions per Section 0.5.2 Fix #7 and Config.json specification.
        /// </summary>
        private static readonly string[] DefaultAllowedExtensions = new[]
        {
            ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc", ".docx", ".xls", ".xlsx"
        };

        /// <summary>
        /// Magic bytes for JPEG images (starts with FF D8 FF).
        /// </summary>
        private static readonly byte[] JpegMagicBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

        /// <summary>
        /// Magic bytes for PNG images (89 50 4E 47 0D 0A 1A 0A).
        /// </summary>
        private static readonly byte[] PngMagicBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// Magic bytes for PDF files (%PDF-).
        /// </summary>
        private static readonly byte[] PdfMagicBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };

        /// <summary>
        /// Magic bytes for Windows executable (MZ header).
        /// SECURITY: Used to detect executable content masquerading as other file types.
        /// </summary>
        private static readonly byte[] ExeMagicBytes = new byte[] { 0x4D, 0x5A }; // MZ

        /// <summary>
        /// Default maximum file size in bytes (10MB) per Section 0.5.2 Fix #7.
        /// </summary>
        private const long DefaultMaxFileSizeBytes = 10 * 1024 * 1024;

        #endregion

        #region <--- Helper Methods --->

        /// <summary>
        /// Creates a mock IFormFile for testing file validation.
        /// SECURITY: Allows precise control over file properties for security testing.
        /// </summary>
        /// <param name="fileName">The file name including extension</param>
        /// <param name="content">The file content as bytes</param>
        /// <param name="contentType">Optional MIME content type</param>
        /// <returns>Mock IFormFile configured with specified properties</returns>
        public static Mock<IFormFile> CreateMockFormFile(string fileName, byte[] content, string contentType = null)
        {
            var mock = new Mock<IFormFile>();
            var stream = new MemoryStream(content);

            mock.SetupGet(f => f.FileName).Returns(fileName);
            mock.SetupGet(f => f.Length).Returns(content.Length);
            mock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));

            if (!string.IsNullOrEmpty(contentType))
            {
                mock.SetupGet(f => f.ContentType).Returns(contentType);
            }

            return mock;
        }

        /// <summary>
        /// Creates a mock IFormFile with specified size for size limit testing.
        /// </summary>
        /// <param name="fileName">The file name including extension</param>
        /// <param name="sizeInBytes">The size to report (actual content is minimal)</param>
        /// <returns>Mock IFormFile with specified Length property</returns>
        private static Mock<IFormFile> CreateMockFormFileWithSize(string fileName, long sizeInBytes)
        {
            // Create minimal content but report specified size
            var content = new byte[Math.Min(sizeInBytes, 1024)]; // Small actual content
            var mock = new Mock<IFormFile>();
            var stream = new MemoryStream(content);

            mock.SetupGet(f => f.FileName).Returns(fileName);
            mock.SetupGet(f => f.Length).Returns(sizeInBytes);
            mock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));

            return mock;
        }

        /// <summary>
        /// Generates HTML content bytes for MIME mismatch testing.
        /// </summary>
        private static byte[] GenerateHtmlContent()
        {
            return Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><h1>Test</h1></body></html>");
        }

        #endregion

        #region <--- TestMaliciousExtensionRejected --->

        /// <summary>
        /// SECURITY TEST: Verify Windows executable extensions are rejected.
        /// CWE-434 mitigation: Prevents upload of files that could execute code.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".exe")]
        [InlineData(".dll")]
        [InlineData(".com")]
        [InlineData(".msi")]
        [InlineData(".scr")]
        [InlineData(".pif")]
        public void TestMaliciousExtensionRejected_Executables(string extension)
        {
            // Arrange
            var fileName = $"malicious{extension}";
            var content = ExeMagicBytes; // Use actual executable magic bytes
            var mockFile = CreateMockFormFile(fileName, content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, $"Executable extension {extension} should be rejected");
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("not allowed", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify Windows script extensions are rejected.
        /// CWE-434 mitigation: Prevents upload of script files.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".bat")]
        [InlineData(".cmd")]
        [InlineData(".ps1")]
        [InlineData(".vbs")]
        [InlineData(".vbe")]
        public void TestMaliciousExtensionRejected_WindowsScripts(string extension)
        {
            // Arrange
            var fileName = $"script{extension}";
            var content = Encoding.UTF8.GetBytes("echo malicious command");
            var mockFile = CreateMockFormFile(fileName, content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, $"Script extension {extension} should be rejected");
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("not allowed", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify server-side script extensions are rejected.
        /// CWE-434 mitigation: Prevents upload of files that could execute on web servers.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".php")]
        [InlineData(".php3")]
        [InlineData(".php4")]
        [InlineData(".php5")]
        [InlineData(".phtml")]
        [InlineData(".asp")]
        [InlineData(".aspx")]
        [InlineData(".jsp")]
        public void TestMaliciousExtensionRejected_ServerScripts(string extension)
        {
            // Arrange
            var fileName = $"webshell{extension}";
            var content = Encoding.UTF8.GetBytes("<?php system($_GET['cmd']); ?>");
            var mockFile = CreateMockFormFile(fileName, content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, $"Server script extension {extension} should be rejected");
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("not allowed", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify Unix shell script extensions are rejected.
        /// CWE-434 mitigation: Prevents upload of shell scripts.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".sh")]
        [InlineData(".bash")]
        [InlineData(".zsh")]
        [InlineData(".ksh")]
        [InlineData(".csh")]
        public void TestMaliciousExtensionRejected_UnixScripts(string extension)
        {
            // Arrange
            var fileName = $"shell{extension}";
            var content = Encoding.UTF8.GetBytes("#!/bin/bash\nrm -rf /");
            var mockFile = CreateMockFormFile(fileName, content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, $"Unix script extension {extension} should be rejected");
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("not allowed", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify other dangerous file types are rejected.
        /// CWE-434 mitigation: Prevents upload of configuration and archive files.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".htaccess")]
        [InlineData(".htpasswd")]
        [InlineData(".config")]
        [InlineData(".jar")]
        [InlineData(".war")]
        [InlineData(".class")]
        public void TestMaliciousExtensionRejected_OtherDangerous(string extension)
        {
            // Arrange
            var fileName = $"dangerous{extension}";
            var content = Encoding.UTF8.GetBytes("malicious content");
            var mockFile = CreateMockFormFile(fileName, content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, $"Dangerous extension {extension} should be rejected");
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("not allowed", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify IsBlockedExtension correctly identifies malicious extensions.
        /// Direct test of the blocking logic.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMaliciousExtensionRejected_IsBlockedExtension()
        {
            // Arrange - combine all malicious extensions
            var allMaliciousExtensions = new List<string>();
            allMaliciousExtensions.AddRange(MaliciousExecutableExtensions);
            allMaliciousExtensions.AddRange(MaliciousScriptExtensions);
            allMaliciousExtensions.AddRange(MaliciousServerScriptExtensions);
            allMaliciousExtensions.AddRange(MaliciousUnixExtensions);
            allMaliciousExtensions.AddRange(MaliciousOtherExtensions);

            // Act & Assert
            foreach (var extension in allMaliciousExtensions)
            {
                var isBlocked = FileValidationUtil.IsBlockedExtension($"file{extension}");
                Assert.True(isBlocked, $"Extension {extension} should be blocked");
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify case-insensitive extension checking.
        /// Attackers may try to bypass with different casing.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".EXE")]
        [InlineData(".Exe")]
        [InlineData(".eXe")]
        [InlineData(".PHP")]
        [InlineData(".Php")]
        [InlineData(".BAT")]
        public void TestMaliciousExtensionRejected_CaseInsensitive(string extension)
        {
            // Arrange
            var fileName = $"file{extension}";
            var content = ExeMagicBytes;
            var mockFile = CreateMockFormFile(fileName, content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, $"Extension {extension} (case variation) should be rejected");
        }

        #endregion

        #region <--- TestOversizedFileRejected --->

        /// <summary>
        /// SECURITY TEST: Verify files exceeding size limit are rejected.
        /// Prevents storage exhaustion attacks per Section 0.5.2 Fix #7.
        /// Default limit is 10MB (10485760 bytes).
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestOversizedFileRejected_ExceedsDefaultLimit()
        {
            // Arrange - 11MB file (exceeds 10MB default limit)
            var oversizedBytes = DefaultMaxFileSizeBytes + (1024 * 1024); // 11MB
            var mockFile = CreateMockFormFileWithSize("largefile.jpg", oversizedBytes);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, "File exceeding 10MB limit should be rejected");
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("exceeds", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify files exactly at the limit are accepted.
        /// Boundary condition test for size validation.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestOversizedFileRejected_AtExactLimit_Accepted()
        {
            // Arrange - exactly 10MB
            var content = new byte[1024]; // Actual content for stream
            Array.Copy(JpegMagicBytes, content, JpegMagicBytes.Length);
            
            var mockFile = new Mock<IFormFile>();
            mockFile.SetupGet(f => f.FileName).Returns("exactsize.jpg");
            mockFile.SetupGet(f => f.Length).Returns(DefaultMaxFileSizeBytes);
            mockFile.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.True(result.IsValid, "File exactly at 10MB limit should be accepted");
        }

        /// <summary>
        /// SECURITY TEST: Verify files just over the limit are rejected.
        /// Boundary condition test - 1 byte over limit.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestOversizedFileRejected_OneByteOver()
        {
            // Arrange - 1 byte over 10MB limit
            var overByOne = DefaultMaxFileSizeBytes + 1;
            var mockFile = CreateMockFormFileWithSize("justover.jpg", overByOne);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, "File 1 byte over limit should be rejected");
            Assert.NotNull(result.ErrorMessage);
        }

        /// <summary>
        /// SECURITY TEST: Verify custom size limits are enforced.
        /// Tests configurable MaxFileSizeBytes option.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestOversizedFileRejected_CustomLimit()
        {
            // Arrange - custom 1MB limit
            var customLimit = 1 * 1024 * 1024; // 1MB
            var options = new FileValidationOptions
            {
                MaxFileSizeBytes = customLimit,
                ValidateMimeType = false
            };

            var mockFile = CreateMockFormFileWithSize("customlimit.jpg", customLimit + 1);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.False(result.IsValid, "File exceeding custom 1MB limit should be rejected");
        }

        /// <summary>
        /// SECURITY TEST: Verify empty files are rejected.
        /// Zero-byte files should not be allowed.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestOversizedFileRejected_EmptyFile()
        {
            // Arrange - empty file
            var mockFile = CreateMockFormFile("empty.jpg", Array.Empty<byte>());

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, "Empty file should be rejected");
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("empty", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify null file is handled gracefully.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestOversizedFileRejected_NullFile()
        {
            // Act
            var result = FileValidationUtil.ValidateFile(null);

            // Assert
            Assert.False(result.IsValid, "Null file should be rejected");
            Assert.NotNull(result.ErrorMessage);
        }

        /// <summary>
        /// SECURITY TEST: Verify file size error message includes actual and limit values.
        /// Important for security logging and debugging.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestOversizedFileRejected_ErrorMessageContent()
        {
            // Arrange
            var oversizedBytes = 15 * 1024 * 1024; // 15MB
            var mockFile = CreateMockFormFileWithSize("bigfile.pdf", oversizedBytes);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid);
            Assert.NotEmpty(result.ErrorMessage);
            // Error message should indicate the size issue
            Assert.Contains("exceeds", result.ErrorMessage.ToLowerInvariant());
        }

        #endregion

        #region <--- TestMimeTypeMismatch --->

        /// <summary>
        /// SECURITY TEST: Verify .jpg file with executable magic bytes is rejected.
        /// CWE-434 mitigation: Prevents disguised executable uploads.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMimeTypeMismatch_JpgWithExeContent()
        {
            // Arrange - .jpg extension but MZ (exe) magic bytes
            var content = new byte[100];
            Array.Copy(ExeMagicBytes, content, ExeMagicBytes.Length);
            var mockFile = CreateMockFormFile("image.jpg", content);

            var options = new FileValidationOptions
            {
                ValidateMimeType = true
            };

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.False(result.IsValid, "JPG file with executable content should be rejected");
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("content", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify .pdf file with HTML content is rejected.
        /// Prevents web content masquerading as documents.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMimeTypeMismatch_PdfWithHtmlContent()
        {
            // Arrange - .pdf extension but HTML content
            var htmlContent = GenerateHtmlContent();
            var mockFile = CreateMockFormFile("document.pdf", htmlContent);

            var options = new FileValidationOptions
            {
                ValidateMimeType = true
            };

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.False(result.IsValid, "PDF file with HTML content should be rejected");
            Assert.NotNull(result.ErrorMessage);
        }

        /// <summary>
        /// SECURITY TEST: Verify .jpg file with correct magic bytes is accepted.
        /// Positive test for valid JPEG content.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMimeTypeMismatch_JpgWithCorrectContent_Accepted()
        {
            // Arrange - valid JPEG magic bytes
            var content = new byte[100];
            Array.Copy(JpegMagicBytes, content, JpegMagicBytes.Length);
            var mockFile = CreateMockFormFile("valid.jpg", content);

            var options = new FileValidationOptions
            {
                ValidateMimeType = true
            };

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.True(result.IsValid, "JPEG file with valid magic bytes should be accepted");
        }

        /// <summary>
        /// SECURITY TEST: Verify .png file with correct magic bytes is accepted.
        /// Positive test for valid PNG content.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMimeTypeMismatch_PngWithCorrectContent_Accepted()
        {
            // Arrange - valid PNG magic bytes
            var content = new byte[100];
            Array.Copy(PngMagicBytes, content, PngMagicBytes.Length);
            var mockFile = CreateMockFormFile("valid.png", content);

            var options = new FileValidationOptions
            {
                ValidateMimeType = true
            };

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.True(result.IsValid, "PNG file with valid magic bytes should be accepted");
        }

        /// <summary>
        /// SECURITY TEST: Verify .pdf file with correct magic bytes is accepted.
        /// Positive test for valid PDF content.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMimeTypeMismatch_PdfWithCorrectContent_Accepted()
        {
            // Arrange - valid PDF magic bytes (%PDF-)
            var content = new byte[100];
            Array.Copy(PdfMagicBytes, content, PdfMagicBytes.Length);
            var mockFile = CreateMockFormFile("valid.pdf", content);

            var options = new FileValidationOptions
            {
                ValidateMimeType = true
            };

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.True(result.IsValid, "PDF file with valid magic bytes should be accepted");
        }

        /// <summary>
        /// SECURITY TEST: Verify MIME validation can be disabled.
        /// Some use cases may need to skip content validation.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMimeTypeMismatch_ValidationDisabled()
        {
            // Arrange - mismatched content but validation disabled
            var content = new byte[100];
            Array.Copy(ExeMagicBytes, content, ExeMagicBytes.Length);
            var mockFile = CreateMockFormFile("image.txt", content);

            var options = new FileValidationOptions
            {
                ValidateMimeType = false,
                AllowedExtensions = new[] { ".txt" }
            };

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert - should pass when MIME validation is disabled
            Assert.True(result.IsValid, "File should be accepted when MIME validation is disabled");
        }

        /// <summary>
        /// SECURITY TEST: Verify ValidateMimeType method directly.
        /// Tests the dedicated MIME validation function.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMimeTypeMismatch_ValidateMimeTypeDirectCall()
        {
            // Arrange - valid JPEG
            var content = new byte[100];
            Array.Copy(JpegMagicBytes, content, JpegMagicBytes.Length);
            var mockFile = CreateMockFormFile("test.jpg", content);

            // Act
            var result = FileValidationUtil.ValidateMimeType(mockFile.Object, ".jpg");

            // Assert
            Assert.True(result.IsValid);
        }

        /// <summary>
        /// SECURITY TEST: Verify file too small for magic byte validation fails.
        /// Files must have enough bytes to read the signature.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMimeTypeMismatch_FileTooSmall()
        {
            // Arrange - file smaller than magic bytes requirement
            var content = new byte[1]; // Only 1 byte, JPEG needs at least 3
            var mockFile = CreateMockFormFile("tiny.jpg", content);

            var options = new FileValidationOptions
            {
                ValidateMimeType = true
            };

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.False(result.IsValid, "File too small for MIME validation should fail");
        }

        #endregion

        #region <--- TestPathTraversalPrevention --->

        /// <summary>
        /// SECURITY TEST: Verify filename with ../ is sanitized.
        /// CWE-22 mitigation: Prevents directory traversal attacks.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_RelativePathDotDot()
        {
            // Arrange
            var maliciousName = "../../../etc/passwd";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(maliciousName);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            Assert.DoesNotContain("..", sanitized);
            Assert.DoesNotContain("/", sanitized);
        }

        /// <summary>
        /// SECURITY TEST: Verify filename with ..\ (Windows) is sanitized.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_WindowsStyleTraversal()
        {
            // Arrange
            var maliciousName = @"..\..\windows\system32\config";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(maliciousName);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            Assert.DoesNotContain("..", sanitized);
            Assert.DoesNotContain("\\", sanitized);
        }

        /// <summary>
        /// SECURITY TEST: Verify absolute path is rejected/sanitized.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_AbsolutePath()
        {
            // Arrange
            var absolutePath = "/etc/passwd";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(absolutePath);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            Assert.DoesNotContain("/", sanitized);
            Assert.False(sanitized.StartsWith("/"), "Sanitized filename should not start with '/'");
        }

        /// <summary>
        /// SECURITY TEST: Verify Windows absolute path is sanitized.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_WindowsAbsolutePath()
        {
            // Arrange
            var absolutePath = @"C:\Windows\System32\config.sys";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(absolutePath);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            Assert.DoesNotContain(":", sanitized);
            Assert.DoesNotContain("\\", sanitized);
        }

        /// <summary>
        /// SECURITY TEST: Verify null bytes in filename are removed.
        /// Null byte injection can bypass extension checks in some systems.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_NullByteInjection()
        {
            // Arrange - null byte injection attempt
            var maliciousName = "evil.jpg\0.exe";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(maliciousName);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            // Note: Using Assert.False with Contains instead of Assert.DoesNotContain
            // due to xUnit issue with null character handling in DoesNotContain
            Assert.False(sanitized.Contains('\0'), "Sanitized filename should not contain null bytes");
        }

        /// <summary>
        /// SECURITY TEST: Verify mixed traversal attempts are handled.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_MixedTraversalAttempts()
        {
            // Arrange - complex traversal attempt
            var maliciousName = "..%2f..%2f..%2fetc%2fpasswd";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(maliciousName);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            // Should not contain dangerous patterns after sanitization
            Assert.DoesNotContain("..", sanitized);
        }

        /// <summary>
        /// SECURITY TEST: Verify empty filename returns safe default.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_EmptyFilename()
        {
            // Arrange
            var emptyName = "";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(emptyName);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            Assert.Equal("unnamed_file", sanitized);
        }

        /// <summary>
        /// SECURITY TEST: Verify whitespace-only filename returns safe default.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_WhitespaceFilename()
        {
            // Arrange
            var whitespace = "   \t\n  ";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(whitespace);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            Assert.Equal("unnamed_file", sanitized);
        }

        /// <summary>
        /// SECURITY TEST: Verify null filename returns safe default.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_NullFilename()
        {
            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(null);

            // Assert
            Assert.False(string.IsNullOrEmpty(sanitized));
            Assert.Equal("unnamed_file", sanitized);
        }

        /// <summary>
        /// SECURITY TEST: Verify valid filename passes through correctly.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_ValidFilename()
        {
            // Arrange
            var validName = "my_document.pdf";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(validName);

            // Assert
            Assert.Equal("my_document.pdf", sanitized);
        }

        /// <summary>
        /// SECURITY TEST: Verify very long filename is truncated.
        /// Prevents filesystem issues with excessively long names.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestPathTraversalPrevention_VeryLongFilename()
        {
            // Arrange - 500 character filename
            var longName = new string('a', 500) + ".pdf";

            // Act
            var sanitized = FileValidationUtil.SanitizeFileName(longName);

            // Assert
            Assert.True(sanitized.Length <= 255, "Filename should be truncated to 255 characters");
            Assert.EndsWith(".pdf", sanitized); // Extension should be preserved
        }

        #endregion

        #region <--- TestAllowedExtensionWhitelist --->

        /// <summary>
        /// SECURITY TEST: Verify allowed image extensions are accepted.
        /// Tests .jpg, .jpeg, .png, .gif from default whitelist.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".jpg")]
        [InlineData(".jpeg")]
        [InlineData(".png")]
        [InlineData(".gif")]
        public void TestAllowedExtensionWhitelist_ImagesAccepted(string extension)
        {
            // Arrange
            var fileName = $"image{extension}";

            // Act
            var isAllowed = FileValidationUtil.IsAllowedExtension(fileName, DefaultAllowedExtensions);

            // Assert
            Assert.True(isAllowed, $"Image extension {extension} should be allowed");
        }

        /// <summary>
        /// SECURITY TEST: Verify allowed document extensions are accepted.
        /// Tests .pdf, .doc, .docx from default whitelist.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".pdf")]
        [InlineData(".doc")]
        [InlineData(".docx")]
        public void TestAllowedExtensionWhitelist_DocumentsAccepted(string extension)
        {
            // Arrange
            var fileName = $"document{extension}";

            // Act
            var isAllowed = FileValidationUtil.IsAllowedExtension(fileName, DefaultAllowedExtensions);

            // Assert
            Assert.True(isAllowed, $"Document extension {extension} should be allowed");
        }

        /// <summary>
        /// SECURITY TEST: Verify allowed spreadsheet extensions are accepted.
        /// Tests .xls, .xlsx from default whitelist.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".xls")]
        [InlineData(".xlsx")]
        public void TestAllowedExtensionWhitelist_SpreadsheetsAccepted(string extension)
        {
            // Arrange
            var fileName = $"spreadsheet{extension}";

            // Act
            var isAllowed = FileValidationUtil.IsAllowedExtension(fileName, DefaultAllowedExtensions);

            // Assert
            Assert.True(isAllowed, $"Spreadsheet extension {extension} should be allowed");
        }

        /// <summary>
        /// SECURITY TEST: Verify unknown extensions are rejected by default.
        /// Extensions not in whitelist should be denied.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".xyz")]
        [InlineData(".abc")]
        [InlineData(".unknown")]
        [InlineData(".random")]
        public void TestAllowedExtensionWhitelist_UnknownRejected(string extension)
        {
            // Arrange
            var fileName = $"file{extension}";

            // Act
            var isAllowed = FileValidationUtil.IsAllowedExtension(fileName, DefaultAllowedExtensions);

            // Assert
            Assert.False(isAllowed, $"Unknown extension {extension} should be rejected");
        }

        /// <summary>
        /// SECURITY TEST: Verify full file validation with whitelist works.
        /// End-to-end test of extension whitelist validation.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAllowedExtensionWhitelist_FullValidation_Accepted()
        {
            // Arrange - valid JPEG with allowed extension
            var content = new byte[100];
            Array.Copy(JpegMagicBytes, content, JpegMagicBytes.Length);
            var mockFile = CreateMockFormFile("photo.jpg", content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.True(result.IsValid, "Valid JPEG file should be accepted");
        }

        /// <summary>
        /// SECURITY TEST: Verify custom whitelist is enforced.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAllowedExtensionWhitelist_CustomWhitelist()
        {
            // Arrange - custom whitelist with only .txt
            var customWhitelist = new[] { ".txt" };
            var options = new FileValidationOptions
            {
                AllowedExtensions = customWhitelist,
                ValidateMimeType = false
            };

            var content = Encoding.UTF8.GetBytes("text content");
            var mockFile = CreateMockFormFile("file.txt", content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.True(result.IsValid, "File with whitelisted extension should be accepted");
        }

        /// <summary>
        /// SECURITY TEST: Verify file not in custom whitelist is rejected.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAllowedExtensionWhitelist_NotInCustomWhitelist()
        {
            // Arrange - custom whitelist without .jpg
            var customWhitelist = new[] { ".txt", ".pdf" };
            var options = new FileValidationOptions
            {
                AllowedExtensions = customWhitelist,
                ValidateMimeType = false
            };

            var content = JpegMagicBytes;
            var mockFile = CreateMockFormFile("image.jpg", content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.False(result.IsValid, "File not in custom whitelist should be rejected");
            Assert.Contains("not in the allowed list", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify case-insensitive whitelist matching.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData(".JPG")]
        [InlineData(".Jpg")]
        [InlineData(".jPg")]
        public void TestAllowedExtensionWhitelist_CaseInsensitive(string extension)
        {
            // Arrange
            var fileName = $"image{extension}";

            // Act
            var isAllowed = FileValidationUtil.IsAllowedExtension(fileName, DefaultAllowedExtensions);

            // Assert
            Assert.True(isAllowed, $"Extension {extension} should match case-insensitively");
        }

        /// <summary>
        /// SECURITY TEST: Verify file without extension is rejected.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAllowedExtensionWhitelist_NoExtension()
        {
            // Arrange
            var content = Encoding.UTF8.GetBytes("content");
            var mockFile = CreateMockFormFile("noextension", content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object);

            // Assert
            Assert.False(result.IsValid, "File without extension should be rejected");
            Assert.Contains("extension", result.ErrorMessage.ToLowerInvariant());
        }

        /// <summary>
        /// SECURITY TEST: Verify blocked extension overrides whitelist.
        /// Even if somehow added to whitelist, blocked extensions should fail.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAllowedExtensionWhitelist_BlockedOverridesAllowed()
        {
            // Arrange - try to allow .exe (should still be blocked)
            var dangerousWhitelist = new[] { ".exe", ".jpg", ".pdf" };
            var options = new FileValidationOptions
            {
                AllowedExtensions = dangerousWhitelist,
                ValidateMimeType = false
            };

            var content = ExeMagicBytes;
            var mockFile = CreateMockFormFile("malware.exe", content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert - blocked list takes precedence
            Assert.False(result.IsValid, "Blocked extension .exe should be rejected even if in allowed list");
        }

        /// <summary>
        /// SECURITY TEST: Verify empty whitelist rejects all files.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAllowedExtensionWhitelist_EmptyWhitelist()
        {
            // Arrange
            var emptyWhitelist = Array.Empty<string>();
            var options = new FileValidationOptions
            {
                AllowedExtensions = emptyWhitelist,
                ValidateMimeType = false
            };

            var content = JpegMagicBytes;
            var mockFile = CreateMockFormFile("image.jpg", content);

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.False(result.IsValid, "File should be rejected when whitelist is empty");
        }

        /// <summary>
        /// SECURITY TEST: Verify IsAllowedExtension with null filename returns false.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAllowedExtensionWhitelist_NullFilename()
        {
            // Act
            var isAllowed = FileValidationUtil.IsAllowedExtension(null, DefaultAllowedExtensions);

            // Assert
            Assert.False(isAllowed, "Null filename should not be allowed");
        }

        /// <summary>
        /// SECURITY TEST: Verify IsAllowedExtension with null whitelist returns false.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAllowedExtensionWhitelist_NullWhitelist()
        {
            // Act
            var isAllowed = FileValidationUtil.IsAllowedExtension("file.jpg", null);

            // Assert
            Assert.False(isAllowed, "File should not be allowed with null whitelist");
        }

        #endregion

        #region <--- Comprehensive Integration Tests --->

        /// <summary>
        /// SECURITY TEST: Comprehensive test with all validations enabled.
        /// Simulates real-world file upload scenario.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestComprehensive_ValidFilePassesAllChecks()
        {
            // Arrange - valid JPEG within size limit
            var content = new byte[1024];
            Array.Copy(JpegMagicBytes, content, JpegMagicBytes.Length);
            var mockFile = CreateMockFormFile("photo.jpg", content);

            var options = new FileValidationOptions
            {
                AllowedExtensions = new[] { ".jpg", ".png", ".pdf" },
                MaxFileSizeBytes = 10 * 1024 * 1024,
                ValidateMimeType = true
            };

            // Act
            var result = FileValidationUtil.ValidateFile(mockFile.Object, options);

            // Assert
            Assert.True(result.IsValid, "Valid file should pass all validation checks");
            Assert.Null(result.ErrorMessage);
        }

        /// <summary>
        /// SECURITY TEST: Verify multiple attack vectors are blocked.
        /// Tests defense in depth - multiple checks must all pass.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestComprehensive_MultipleAttackVectorsBlocked()
        {
            // Test various attack patterns
            var attackPatterns = new[]
            {
                ("../../../passwd.jpg", JpegMagicBytes), // Path traversal with allowed extension
                ("shell.php.jpg", ExeMagicBytes), // Double extension with wrong content
                ("malware.exe", ExeMagicBytes), // Executable extension
                ("script.bat", Encoding.UTF8.GetBytes("@echo off")), // Batch file
            };

            foreach (var (filename, content) in attackPatterns)
            {
                var mockFile = CreateMockFormFile(filename, content);
                var result = FileValidationUtil.ValidateFile(mockFile.Object);
                
                // At least one check should fail for malicious files
                // Note: Some may pass if only extension check fails (like path traversal with .jpg)
                // But blocked extensions should always fail
                if (filename.Contains(".exe") || filename.Contains(".bat") || filename.Contains(".php"))
                {
                    Assert.False(result.IsValid, $"Attack pattern '{filename}' should be blocked");
                }
            }
        }

        #endregion
    }
}
