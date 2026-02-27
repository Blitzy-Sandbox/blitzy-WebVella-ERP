/**
 * Vitest Component Tests for `<MultiFileUploadField />`
 *
 * Validates the React MultiFileUploadField component
 * (`apps/frontend/src/components/fields/MultiFileUploadField.tsx`) that replaces
 * the monolith's `PcFieldMultiFileUpload` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldMultiFileUpload/PcFieldMultiFileUpload.cs`)
 * and `WvFieldUserFileMultiple` TagHelper.
 *
 * The monolith's PcFieldMultiFileUploadOptions extends PcFieldBaseOptions with:
 *   - Accept (string): MIME-type filter
 *   - GetHandlerPrefix (default "/fs"): URL prefix for file downloads
 *   - FileUploadApi (default "/fs/upload-file-multiple"): Upload endpoint
 *   - PathPropName (default "path"), SizePropName (default "size"),
 *     NamePropName (default "name"), IconPropName (default "icon"),
 *     TimestampPropName (default "timestamp"), AuthorPropName (default "author")
 *     — configurable property name mapping for raw metadata objects
 *
 * Test coverage spans:
 *   - Display mode: file list table (name, size, date), download links, file type
 *     icons, empty-value messages for null and empty array
 *   - Edit mode: file list table with existing files, "Add files" button, drag-
 *     and-drop zone, per-file upload progress, remove buttons, onChange callbacks
 *   - File list management: display, add, remove, batch upload
 *   - Configurable property names: default and custom prop name mappings
 *   - Value parsing: FileMetadata[], JSON string, null
 *   - Access control: full / readonly / forbidden
 *   - Validation: error message, validation errors
 *   - Visibility: isVisible true / false
 *
 * @see apps/frontend/src/components/fields/MultiFileUploadField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldMultiFileUpload/PcFieldMultiFileUpload.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import MultiFileUploadField from '../../../src/components/fields/MultiFileUploadField';
import type {
  MultiFileUploadFieldProps,
  FileMetadata,
} from '../../../src/components/fields/MultiFileUploadField';

// ---------------------------------------------------------------------------
// Mock — apiClient
// ---------------------------------------------------------------------------

/**
 * Mock the centralized apiClient module so that upload POSTs do not
 * hit a real server. Individual tests override `mockPost.mockResolvedValue`
 * or `mockPost.mockRejectedValue` as needed.
 */
const mockPost = vi.fn();

vi.mock('../../../src/api/client', () => ({
  __esModule: true,
  default: {
    post: (...args: unknown[]) => mockPost(...args),
  },
}));

// ---------------------------------------------------------------------------
// Test Fixtures
// ---------------------------------------------------------------------------

/** Canonical FileMetadata fixture with all properties populated. */
const sampleFile1: FileMetadata = {
  path: '/uploads/report.pdf',
  name: 'report.pdf',
  size: 204800,
  icon: 'fas fa-file-pdf',
  timestamp: '2024-06-15T10:30:00.000Z',
  author: 'admin',
};

const sampleFile2: FileMetadata = {
  path: '/uploads/image.png',
  name: 'image.png',
  size: 51200,
  icon: 'fas fa-file-image',
  timestamp: '2024-07-01T14:00:00.000Z',
  author: 'user1',
};

const sampleFile3: FileMetadata = {
  path: '/uploads/spreadsheet.xlsx',
  name: 'spreadsheet.xlsx',
  size: 102400,
  timestamp: '2024-08-10T09:15:00.000Z',
};

/** Creates a complete default set of props for MultiFileUploadField. */
function createDefaultProps(
  overrides: Partial<MultiFileUploadFieldProps> = {},
): MultiFileUploadFieldProps {
  return {
    name: 'multi_file_field',
    value: null,
    ...overrides,
  };
}

/**
 * Helper: Create a File object for use in upload / drop simulations.
 * The browser File constructor is available in jsdom.
 */
function createMockFile(
  fileName: string,
  sizeBytes: number,
  mimeType: string,
): File {
  const content = new Uint8Array(sizeBytes);
  return new File([content], fileName, { type: mimeType });
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('MultiFileUploadField', () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('renders file list table with name, size, date', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1, sampleFile2],
            mode: 'display',
          })}
        />,
      );

      // File names should be present
      expect(screen.getByText('report.pdf')).toBeInTheDocument();
      expect(screen.getByText('image.png')).toBeInTheDocument();

      // Size column — formatFileSize(204800) = "200.0 KB", formatFileSize(51200) = "50.0 KB"
      expect(screen.getByText('200.0 KB')).toBeInTheDocument();
      expect(screen.getByText('50.0 KB')).toBeInTheDocument();

      // Table header columns should appear
      expect(screen.getByText('File')).toBeInTheDocument();
      expect(screen.getByText('Size')).toBeInTheDocument();
      expect(screen.getByText('Date')).toBeInTheDocument();
    });

    it('renders download links for each file', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'display',
            srcPrefix: '/fs',
          })}
        />,
      );

      // In display mode, file names are rendered as links
      const link = screen.getByRole('link', { name: /report\.pdf/i });
      expect(link).toBeInTheDocument();
      expect(link).toHaveAttribute('href', '/fs/uploads/report.pdf');
      expect(link).toHaveAttribute('target', '_blank');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    });

    it('shows file type icons based on extension', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1, sampleFile2],
            mode: 'display',
          })}
        />,
      );

      // Icons are rendered as <i> elements with icon classes.
      // sampleFile1.icon = 'fas fa-file-pdf', sampleFile2.icon = 'fas fa-file-image'
      const icons = document.querySelectorAll('i[aria-hidden="true"]');
      expect(icons.length).toBeGreaterThanOrEqual(2);

      const iconClasses = Array.from(icons).map((el) => el.className);
      expect(iconClasses.some((cls) => cls.includes('fa-file-pdf'))).toBe(true);
      expect(iconClasses.some((cls) => cls.includes('fa-file-image'))).toBe(true);
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: null,
            mode: 'display',
            emptyValueMessage: 'No files uploaded',
          })}
        />,
      );

      expect(screen.getByText('No files uploaded')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty array', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'display',
            emptyValueMessage: 'No files',
          })}
        />,
      );

      expect(screen.getByText('No files')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('renders file list table with existing files', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1, sampleFile2],
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      expect(screen.getByText('report.pdf')).toBeInTheDocument();
      expect(screen.getByText('image.png')).toBeInTheDocument();

      // In edit mode file names are rendered as <span> rather than <a>
      const links = screen.queryAllByRole('link');
      // No download links in edit mode — names are shown as plain text
      expect(links.length).toBe(0);
    });

    it('shows "Add files" button triggering <input type="file" multiple>', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      // The hidden file input should exist
      const fileInput = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement | null;
      expect(fileInput).not.toBeNull();
      expect(fileInput!.multiple).toBe(true);
    });

    it('renders drag-and-drop zone', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      // Drop zone has role="button" and descriptive aria-label
      const dropZone = screen.getByRole('button', {
        name: /drag and drop files here or click to browse/i,
      });
      expect(dropZone).toBeInTheDocument();
    });

    it('shows per-file upload progress indicator', async () => {
      // Simulate a slow upload that triggers progress callbacks
      let progressCallback: ((event: { loaded: number; total: number }) => void) | undefined;

      mockPost.mockImplementation(
        (_url: string, _formData: FormData, config: { onUploadProgress?: (event: { loaded: number; total: number }) => void }) => {
          progressCallback = config?.onUploadProgress;
          // Return a promise that resolves after we call the progress callback
          return new Promise((resolve) => {
            setTimeout(() => {
              resolve({
                data: {
                  object: {
                    path: '/uploads/test-upload.pdf',
                    name: 'test-upload.pdf',
                    size: 1024,
                  },
                },
              });
            }, 50);
          });
        },
      );

      const onChange = vi.fn();
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            onChange,
          })}
        />,
      );

      const fileInput = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;

      const testFile = createMockFile('test-upload.pdf', 1024, 'application/pdf');
      // Simulate file input change
      Object.defineProperty(fileInput, 'files', {
        value: [testFile],
        writable: false,
      });
      fireEvent.change(fileInput);

      // The upload mock was called
      expect(mockPost).toHaveBeenCalled();

      // Trigger upload progress if the callback was captured
      if (progressCallback) {
        progressCallback({ loaded: 512, total: 1024 });
      }

      // Look for progress indicator — it renders as a progressbar role
      const progressBars = document.querySelectorAll('[role="progressbar"]');
      expect(progressBars.length).toBeGreaterThanOrEqual(1);
    });

    it('shows remove button per file', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1, sampleFile2],
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      // Each file should have a remove button with aria-label "Remove {name}"
      const removeBtn1 = screen.getByRole('button', { name: /Remove report\.pdf/i });
      const removeBtn2 = screen.getByRole('button', { name: /Remove image\.png/i });
      expect(removeBtn1).toBeInTheDocument();
      expect(removeBtn2).toBeInTheDocument();
    });

    it('calls onChange with updated file array on remove', async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1, sampleFile2],
            mode: 'edit',
            access: 'full',
            onChange,
          })}
        />,
      );

      // Click remove on the first file
      const removeBtn = screen.getByRole('button', { name: /Remove report\.pdf/i });
      await user.click(removeBtn);

      // onChange should have been called with only the second file
      expect(onChange).toHaveBeenCalledTimes(1);
      const updatedFiles = onChange.mock.calls[0][0] as FileMetadata[];
      expect(updatedFiles).toHaveLength(1);
      expect(updatedFiles[0].name).toBe('image.png');
    });
  });

  // =========================================================================
  // File List Management
  // =========================================================================

  describe('file list management', () => {
    it('displays existing files from value array', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1, sampleFile2, sampleFile3],
            mode: 'display',
          })}
        />,
      );

      expect(screen.getByText('report.pdf')).toBeInTheDocument();
      expect(screen.getByText('image.png')).toBeInTheDocument();
      expect(screen.getByText('spreadsheet.xlsx')).toBeInTheDocument();
    });

    it('adds new files to list on upload', async () => {
      const uploadedMeta = {
        path: '/uploads/new-file.pdf',
        name: 'new-file.pdf',
        size: 2048,
      };

      mockPost.mockResolvedValue({
        data: { object: uploadedMeta },
      });

      const onChange = vi.fn();
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'edit',
            access: 'full',
            onChange,
          })}
        />,
      );

      const fileInput = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;

      const testFile = createMockFile('new-file.pdf', 2048, 'application/pdf');
      Object.defineProperty(fileInput, 'files', {
        value: [testFile],
        writable: false,
      });
      fireEvent.change(fileInput);

      // Wait for the async upload to resolve
      await vi.waitFor(() => {
        expect(onChange).toHaveBeenCalled();
      });

      // The onChange should include the existing file PLUS the newly uploaded file
      const updatedFiles = onChange.mock.calls[0][0] as FileMetadata[];
      expect(updatedFiles.length).toBeGreaterThanOrEqual(2);
      expect(updatedFiles[0].name).toBe('report.pdf');
      expect(updatedFiles[1].name).toBe('new-file.pdf');
    });

    it('removes file from list on remove click', async () => {
      const user = userEvent.setup();
      const onChange = vi.fn();

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1, sampleFile2, sampleFile3],
            mode: 'edit',
            access: 'full',
            onChange,
          })}
        />,
      );

      // Remove the second file (image.png)
      const removeBtn = screen.getByRole('button', { name: /Remove image\.png/i });
      await user.click(removeBtn);

      expect(onChange).toHaveBeenCalledTimes(1);
      const remaining = onChange.mock.calls[0][0] as FileMetadata[];
      expect(remaining).toHaveLength(2);
      expect(remaining[0].name).toBe('report.pdf');
      expect(remaining[1].name).toBe('spreadsheet.xlsx');
    });

    it('handles batch upload of multiple files', async () => {
      mockPost.mockResolvedValue({
        data: {
          object: {
            path: '/uploads/batch-file.txt',
            name: 'batch-file.txt',
            size: 512,
          },
        },
      });

      const onChange = vi.fn();
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            onChange,
          })}
        />,
      );

      const fileInput = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;

      const file1 = createMockFile('file1.txt', 512, 'text/plain');
      const file2 = createMockFile('file2.txt', 1024, 'text/plain');
      const file3 = createMockFile('file3.txt', 256, 'text/plain');

      Object.defineProperty(fileInput, 'files', {
        value: [file1, file2, file3],
        writable: false,
      });
      fireEvent.change(fileInput);

      // apiClient.post should be called 3 times (once per file)
      await vi.waitFor(() => {
        expect(mockPost).toHaveBeenCalledTimes(3);
      });

      // onChange should be called with all uploaded files
      await vi.waitFor(() => {
        expect(onChange).toHaveBeenCalled();
      });
    });
  });

  // =========================================================================
  // Configurable Property Names
  // =========================================================================

  describe('configurable property names', () => {
    it('uses default property names (path, name, size, etc.)', () => {
      const files: FileMetadata[] = [
        {
          path: '/default/test.pdf',
          name: 'test.pdf',
          size: 1024,
          icon: 'fas fa-file-pdf',
          timestamp: '2024-01-01T00:00:00.000Z',
          author: 'admin',
        },
      ];

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: files,
            mode: 'display',
          })}
        />,
      );

      expect(screen.getByText('test.pdf')).toBeInTheDocument();
      expect(screen.getByText('1.0 KB')).toBeInTheDocument();
    });

    it('uses custom pathPropName when provided', () => {
      // Provide raw objects with a custom path key
      const rawValue = [
        {
          url: '/custom/path.pdf',
          name: 'path.pdf',
          size: 2048,
        },
      ];

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: rawValue as unknown as FileMetadata[],
            mode: 'display',
            pathPropName: 'url',
            srcPrefix: '/fs',
          })}
        />,
      );

      // The component should map 'url' → path, so the download link has the
      // custom path value
      const link = screen.getByRole('link', { name: /path\.pdf/i });
      expect(link).toHaveAttribute('href', '/fs/custom/path.pdf');
    });

    it('uses custom namePropName when provided', () => {
      const rawValue = [
        {
          path: '/test/doc.pdf',
          title: 'Custom Title',
          size: 4096,
        },
      ];

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: rawValue as unknown as FileMetadata[],
            mode: 'display',
            namePropName: 'title',
          })}
        />,
      );

      expect(screen.getByText('Custom Title')).toBeInTheDocument();
    });

    it('uses custom sizePropName when provided', () => {
      const rawValue = [
        {
          path: '/test/file.txt',
          name: 'file.txt',
          filesize: 1048576, // 1 MB
        },
      ];

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: rawValue as unknown as FileMetadata[],
            mode: 'display',
            sizePropName: 'filesize',
          })}
        />,
      );

      // 1048576 bytes = 1024.0 KB → 1.0 MB
      expect(screen.getByText('1.0 MB')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Value Parsing
  // =========================================================================

  describe('value parsing', () => {
    it('handles value as FileMetadata array', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1, sampleFile2],
            mode: 'display',
          })}
        />,
      );

      expect(screen.getByText('report.pdf')).toBeInTheDocument();
      expect(screen.getByText('image.png')).toBeInTheDocument();
    });

    it('handles value as JSON string (parses to array)', () => {
      const jsonString = JSON.stringify([
        { path: '/json/doc.pdf', name: 'doc.pdf', size: 5120 },
        { path: '/json/img.jpg', name: 'img.jpg', size: 10240 },
      ]);

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: jsonString,
            mode: 'display',
          })}
        />,
      );

      expect(screen.getByText('doc.pdf')).toBeInTheDocument();
      expect(screen.getByText('img.jpg')).toBeInTheDocument();
      expect(screen.getByText('5.0 KB')).toBeInTheDocument();
      expect(screen.getByText('10.0 KB')).toBeInTheDocument();
    });

    it('handles null value', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: null,
            mode: 'display',
            emptyValueMessage: 'Nothing here',
          })}
        />,
      );

      expect(screen.getByText('Nothing here')).toBeInTheDocument();
    });

    it('handles empty string value', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: '' as unknown as null,
            mode: 'display',
            emptyValueMessage: 'Empty',
          })}
        />,
      );

      expect(screen.getByText('Empty')).toBeInTheDocument();
    });

    it('handles malformed JSON string gracefully', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: '{broken json',
            mode: 'display',
            emptyValueMessage: 'No files',
          })}
        />,
      );

      // Should fall back to empty — malformed JSON is treated as no files
      expect(screen.getByText('No files')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      // File should be visible
      expect(screen.getByText('report.pdf')).toBeInTheDocument();

      // Drop zone should be present in edit mode with full access
      const dropZone = screen.getByRole('button', {
        name: /drag and drop files here or click to browse/i,
      });
      expect(dropZone).toBeInTheDocument();

      // Remove button should be available
      const removeBtn = screen.getByRole('button', { name: /Remove report\.pdf/i });
      expect(removeBtn).toBeInTheDocument();
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'edit',
            access: 'readonly',
          })}
        />,
      );

      // File should still be visible
      expect(screen.getByText('report.pdf')).toBeInTheDocument();

      // Drop zone should NOT be present because isEditMode = false when
      // access is readonly
      const dropZone = screen.queryByRole('button', {
        name: /drag and drop files here or click to browse/i,
      });
      expect(dropZone).not.toBeInTheDocument();

      // No remove buttons when readonly
      const removeBtn = screen.queryByRole('button', {
        name: /Remove report\.pdf/i,
      });
      expect(removeBtn).not.toBeInTheDocument();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'edit',
            access: 'forbidden',
            accessDeniedMessage: 'Access denied',
          })}
        />,
      );

      // Should display the access denied message
      expect(screen.getByText('Access denied')).toBeInTheDocument();

      // File should NOT be rendered
      expect(screen.queryByText('report.pdf')).not.toBeInTheDocument();
    });

    it('renders custom access denied message', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'edit',
            access: 'forbidden',
            accessDeniedMessage: 'You do not have permission',
          })}
        />,
      );

      expect(
        screen.getByText('You do not have permission'),
      ).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'edit',
            access: 'full',
            error: 'File upload is required',
          })}
        />,
      );

      const errorMsg = screen.getByRole('alert');
      expect(errorMsg).toBeInTheDocument();
      expect(errorMsg).toHaveTextContent('File upload is required');
    });

    it('shows validation error styling', () => {
      const { container } = render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            error: 'At least one file is required',
          })}
        />,
      );

      // The error prop triggers a red border on the file list container
      const borderedDiv = container.querySelector('.border-red-300');
      expect(borderedDiv).toBeInTheDocument();

      // Error text should be visible
      expect(
        screen.getByText('At least one file is required'),
      ).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'display',
            isVisible: true,
          })}
        />,
      );

      expect(screen.getByText('report.pdf')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'display',
            isVisible: false,
          })}
        />,
      );

      // The component should return null, so nothing renders
      expect(container.innerHTML).toBe('');
      expect(screen.queryByText('report.pdf')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Drag-and-Drop
  // =========================================================================

  describe('drag-and-drop', () => {
    it('highlights drop zone on drag enter', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      const dropZone = screen.getByRole('button', {
        name: /drag and drop files here or click to browse/i,
      });

      // Simulate dragOver to activate highlight
      fireEvent.dragOver(dropZone);

      // The component applies border-blue-400 and bg-blue-50 classes on dragOver
      expect(dropZone.className).toContain('border-blue-400');
      expect(dropZone.className).toContain('bg-blue-50');
    });

    it('removes highlight on drag leave', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      const dropZone = screen.getByRole('button', {
        name: /drag and drop files here or click to browse/i,
      });

      // Drag over then leave
      fireEvent.dragOver(dropZone);
      expect(dropZone.className).toContain('border-blue-400');

      fireEvent.dragLeave(dropZone);
      expect(dropZone.className).not.toContain('border-blue-400');
      expect(dropZone.className).toContain('border-gray-300');
    });

    it('processes dropped files', async () => {
      mockPost.mockResolvedValue({
        data: {
          object: {
            path: '/uploads/dropped.pdf',
            name: 'dropped.pdf',
            size: 3072,
          },
        },
      });

      const onChange = vi.fn();
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            onChange,
          })}
        />,
      );

      const dropZone = screen.getByRole('button', {
        name: /drag and drop files here or click to browse/i,
      });

      const droppedFile = createMockFile(
        'dropped.pdf',
        3072,
        'application/pdf',
      );

      // Simulate drop event with dataTransfer.files
      fireEvent.drop(dropZone, {
        dataTransfer: {
          files: [droppedFile],
        },
      });

      // The upload should have been triggered
      await vi.waitFor(() => {
        expect(mockPost).toHaveBeenCalledTimes(1);
      });

      // Verify the first argument is the upload API endpoint
      expect(mockPost.mock.calls[0][0]).toBe('/fs/upload-file-multiple');

      // Verify FormData was sent
      const formDataArg = mockPost.mock.calls[0][1];
      expect(formDataArg).toBeInstanceOf(FormData);
    });

    it('does not process drop in readonly mode', () => {
      const onChange = vi.fn();
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'edit',
            access: 'readonly',
            onChange,
          })}
        />,
      );

      // In readonly mode, no drop zone should be rendered
      const dropZone = screen.queryByRole('button', {
        name: /drag and drop files here or click to browse/i,
      });
      expect(dropZone).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Upload API Integration (via mocked apiClient.post)
  // =========================================================================

  describe('upload API integration', () => {
    it('calls apiClient.post with FormData and onUploadProgress', async () => {
      mockPost.mockResolvedValue({
        data: {
          object: {
            path: '/uploads/api-test.pdf',
            name: 'api-test.pdf',
            size: 4096,
          },
        },
      });

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            fileUploadApi: '/custom/upload-endpoint',
            onChange: vi.fn(),
          })}
        />,
      );

      const fileInput = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;

      const testFile = createMockFile(
        'api-test.pdf',
        4096,
        'application/pdf',
      );
      Object.defineProperty(fileInput, 'files', {
        value: [testFile],
        writable: false,
      });
      fireEvent.change(fileInput);

      await vi.waitFor(() => {
        expect(mockPost).toHaveBeenCalledTimes(1);
      });

      // Verify the custom endpoint was used
      expect(mockPost.mock.calls[0][0]).toBe('/custom/upload-endpoint');

      // Verify FormData was passed
      expect(mockPost.mock.calls[0][1]).toBeInstanceOf(FormData);

      // Verify config includes onUploadProgress callback
      const config = mockPost.mock.calls[0][2] as Record<string, unknown>;
      expect(typeof config.onUploadProgress).toBe('function');
    });

    it('handles upload failure gracefully', async () => {
      mockPost.mockRejectedValue(new Error('Network error'));

      const onChange = vi.fn();
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            onChange,
          })}
        />,
      );

      const fileInput = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;

      const testFile = createMockFile('fail.pdf', 1024, 'application/pdf');
      Object.defineProperty(fileInput, 'files', {
        value: [testFile],
        writable: false,
      });
      fireEvent.change(fileInput);

      // Wait for the upload promise to resolve (will catch and show error)
      await vi.waitFor(() => {
        expect(mockPost).toHaveBeenCalledTimes(1);
      });

      // onChange should NOT have been called since the upload failed
      // (only successful uploads are added to the file list)
      // Give it a brief window to ensure no delayed call
      await new Promise((r) => setTimeout(r, 100));
      expect(onChange).not.toHaveBeenCalled();
    });
  });

  // =========================================================================
  // Accept Attribute
  // =========================================================================

  describe('accept attribute', () => {
    it('passes accept prop to the file input', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            accept: 'image/*,.pdf',
          })}
        />,
      );

      const fileInput = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      expect(fileInput).not.toBeNull();
      expect(fileInput!.accept).toBe('image/*,.pdf');
    });

    it('shows accepted formats text in drop zone', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            accept: '.pdf,.docx',
          })}
        />,
      );

      expect(
        screen.getByText(/Accepted formats: \.pdf,\.docx/i),
      ).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Disabled State
  // =========================================================================

  describe('disabled state', () => {
    it('disables remove buttons when disabled prop is true', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'edit',
            access: 'full',
            disabled: true,
          })}
        />,
      );

      // When disabled, the component doesn't render the edit mode controls
      // because isEditMode = mode === 'edit' && access === 'full' && !disabled
      // So there should be no remove buttons or drop zone
      const removeBtn = screen.queryByRole('button', {
        name: /Remove report\.pdf/i,
      });
      expect(removeBtn).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // srcPrefix / Download URL Building
  // =========================================================================

  describe('download URL building', () => {
    it('prepends srcPrefix to file path', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'display',
            srcPrefix: '/api/files',
          })}
        />,
      );

      const link = screen.getByRole('link', { name: /report\.pdf/i });
      expect(link).toHaveAttribute('href', '/api/files/uploads/report.pdf');
    });

    it('handles absolute URL paths (http/https) without prefix', () => {
      const fileWithAbsUrl: FileMetadata = {
        path: 'https://cdn.example.com/uploads/report.pdf',
        name: 'report.pdf',
        size: 204800,
      };

      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [fileWithAbsUrl],
            mode: 'display',
            srcPrefix: '/fs',
          })}
        />,
      );

      const link = screen.getByRole('link', { name: /report\.pdf/i });
      // Absolute URLs should be used as-is
      expect(link).toHaveAttribute(
        'href',
        'https://cdn.example.com/uploads/report.pdf',
      );
    });

    it('handles trailing slash in srcPrefix', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [sampleFile1],
            mode: 'display',
            srcPrefix: '/fs/',
          })}
        />,
      );

      const link = screen.getByRole('link', { name: /report\.pdf/i });
      // Should not produce double slashes: /fs//uploads/... → /fs/uploads/...
      expect(link).toHaveAttribute('href', '/fs/uploads/report.pdf');
    });
  });

  // =========================================================================
  // Description
  // =========================================================================

  describe('description', () => {
    it('renders description text when provided', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            description: 'Upload your documents here',
          })}
        />,
      );

      expect(
        screen.getByText('Upload your documents here'),
      ).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Label Rendering
  // =========================================================================

  describe('label rendering', () => {
    it('renders label when provided', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'display',
            label: 'Attachments',
          })}
        />,
      );

      expect(screen.getByText('Attachments')).toBeInTheDocument();
    });

    it('shows required indicator on label', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'edit',
            access: 'full',
            label: 'Documents',
            required: true,
          })}
        />,
      );

      expect(screen.getByText('Documents')).toBeInTheDocument();
      // The required indicator '*' should be present
      expect(screen.getByText('*')).toBeInTheDocument();
    });

    it('hides label when labelMode is "hidden"', () => {
      render(
        <MultiFileUploadField
          {...createDefaultProps({
            value: [],
            mode: 'display',
            label: 'Hidden Label',
            labelMode: 'hidden',
          })}
        />,
      );

      expect(screen.queryByText('Hidden Label')).not.toBeInTheDocument();
    });
  });
});
