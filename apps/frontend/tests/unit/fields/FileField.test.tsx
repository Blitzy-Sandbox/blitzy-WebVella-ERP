/**
 * Vitest Component Tests for `<FileField />`
 *
 * Validates the React FileField component
 * (`apps/frontend/src/components/fields/FileField.tsx`) that replaces
 * the monolith's `PcFieldFile` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldFile/PcFieldFile.cs`).
 *
 * The monolith's PcFieldFileOptions extends PcFieldBaseOptions with:
 *   - Accept (string): MIME-type filter for file input
 *
 * Test coverage spans:
 *   - Display mode: file name as download link, URL construction from
 *     srcPrefix + value, file type icon rendering, emptyValueMessage for
 *     null/empty values
 *   - Edit mode: file input rendering, drag-and-drop zone, current file
 *     display with remove button, accept filter, upload with onChange
 *     callback, progress bar during upload
 *   - File upload: trigger on selection, progress state, success/error
 *     handling, accept filter enforcement
 *   - Drag and drop: drag-over indicator, dropped file acceptance
 *   - Remove functionality: remove button, onChange(null) callback
 *   - Access control: full / readonly / forbidden
 *   - Validation: error messages, validation errors
 *   - Null/empty handling: null value, undefined value
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/FileField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldFile/PcFieldFile.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  render,
  screen,
  fireEvent,
  within,
  cleanup,
  waitFor,
} from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import FileField from '../../../src/components/fields/FileField';
import type { FileFieldProps } from '../../../src/components/fields/FileField';

// ---------------------------------------------------------------------------
// Mock — apiClient
// ---------------------------------------------------------------------------

/**
 * Mock the centralized apiClient module so that FormData POST uploads,
 * presigned URL GET requests, and S3 PUT uploads do not hit a real server.
 * Individual tests override the mock implementations as needed.
 */
const mockGet = vi.fn();
const mockPost = vi.fn();
const mockPut = vi.fn();

vi.mock('../../../src/api/client', () => ({
  __esModule: true,
  default: {
    get: (...args: unknown[]) => mockGet(...args),
    post: (...args: unknown[]) => mockPost(...args),
    put: (...args: unknown[]) => mockPut(...args),
  },
}));

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default set of FileFieldProps for consistent test setup.
 * Mirrors the PcFieldFileOptions defaults from PcFieldFile.cs:
 *   - Accept → undefined (all file types allowed)
 *   - srcPrefix → undefined (uses component default)
 *   - mode → "display" (default)
 */
function createDefaultProps(
  overrides: Partial<FileFieldProps> = {},
  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- FileField
  // accepts BaseFieldProps at the type level (for FieldRenderer compatibility)
  // then internally casts to FileFieldProps. Use `any` so the narrower
  // onChange signature doesn't conflict with BaseFieldProps.onChange.
): any {
  return {
    name: 'test_file',
    value: null,
    ...overrides,
  };
}

/**
 * Helper: Create a File object for use in upload and drop simulations.
 * The browser File constructor is available in jsdom.
 */
function createMockFile(
  fileName: string = 'document.pdf',
  sizeBytes: number = 1024,
  mimeType: string = 'application/pdf',
): File {
  const content = new Uint8Array(sizeBytes);
  return new File([content], fileName, { type: mimeType });
}

/** Sample file path fixture used across display mode tests. */
const SAMPLE_FILE_PATH = '/uploads/documents/report.pdf';

/** Sample srcPrefix for constructing full download URLs. */
const SAMPLE_SRC_PREFIX = 'https://cdn.example.com';

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('FileField', () => {
  beforeEach(() => {
    mockGet.mockReset();
    mockPost.mockReset();
    mockPut.mockReset();
  });

  afterEach(() => {
    cleanup();
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('shows file name as download link', () => {
      render(
        <FileField
          {...createDefaultProps({
            value: SAMPLE_FILE_PATH,
            mode: 'display',
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toBeInTheDocument();
      expect(link).toHaveTextContent('report.pdf');
      expect(link).toHaveAttribute('download', 'report.pdf');
      expect(link).toHaveAttribute('aria-label', 'Download report.pdf');
    });

    it('constructs download URL from srcPrefix + value', () => {
      render(
        <FileField
          {...createDefaultProps({
            value: SAMPLE_FILE_PATH,
            srcPrefix: SAMPLE_SRC_PREFIX,
            mode: 'display',
          })}
        />,
      );

      const link = screen.getByRole('link');
      // srcPrefix="https://cdn.example.com" + value="/uploads/documents/report.pdf"
      // separator="" because value starts with "/"
      expect(link).toHaveAttribute(
        'href',
        `${SAMPLE_SRC_PREFIX}${SAMPLE_FILE_PATH}`,
      );
      // Verify target="_blank" for external download
      expect(link).toHaveAttribute('target', '_blank');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    });

    it('shows file type icon based on extension', () => {
      const { container } = render(
        <FileField
          {...createDefaultProps({
            value: '/uploads/test-file.pdf',
            mode: 'display',
          })}
        />,
      );

      // FileTypeIcon for pdf uses text-red-500 color class
      const svgs = container.querySelectorAll('svg');
      const pdfIcon = Array.from(svgs).find((svg) =>
        svg.classList.contains('text-red-500'),
      );
      expect(pdfIcon).toBeTruthy();

      // Verify that at least two SVGs are rendered (FileTypeIcon + DownloadIcon)
      expect(svgs.length).toBeGreaterThanOrEqual(2);
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <FileField
          {...createDefaultProps({
            value: null,
            mode: 'display',
          })}
        />,
      );

      const emptyMsg = screen.getByText('no data');
      expect(emptyMsg).toBeInTheDocument();
      expect(emptyMsg).toHaveClass('italic');
      expect(emptyMsg.tagName.toLowerCase()).toBe('span');
      // No download link should be present
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(
        <FileField
          {...createDefaultProps({
            value: '',
            mode: 'display',
          })}
        />,
      );

      // The component checks `if (!value)` which catches empty string
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('renders a file input in edit mode', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
          })}
        />,
      );

      // Hidden file input with sr-only class
      const input = document.querySelector('input[type="file"]');
      expect(input).toBeInTheDocument();
      expect(input).toHaveClass('sr-only');
      expect(input).toHaveAttribute('tabindex', '-1');
    });

    it('renders drag-and-drop zone', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
          })}
        />,
      );

      // The drag-and-drop zone has role="button"
      const dropZone = screen.getByRole('button', {
        name: /drag and drop/i,
      });
      expect(dropZone).toBeInTheDocument();
      expect(dropZone).toHaveTextContent(/click to upload/i);
      expect(dropZone).toHaveTextContent(/drag and drop/i);
    });

    it('shows current file (if value exists) with filename and remove button', () => {
      const onChange = vi.fn();
      render(
        <FileField
          {...createDefaultProps({
            value: '/uploads/test-report.pdf',
            mode: 'edit',
            onChange,
          })}
        />,
      );

      // Current file display has role="status" with accessible name
      const fileStatus = screen.getByRole('status', {
        name: /current file: test-report\.pdf/i,
      });
      expect(fileStatus).toBeInTheDocument();

      // Within that container, filename should be displayed
      const statusContainer = within(fileStatus);
      expect(statusContainer.getByText('test-report.pdf')).toBeInTheDocument();

      // Remove button should be present with accessible label
      const removeButton = screen.getByRole('button', {
        name: /remove file test-report\.pdf/i,
      });
      expect(removeButton).toBeInTheDocument();
    });

    it('applies accept filter to file input', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            accept: 'image/*,.pdf',
          })}
        />,
      );

      const input = document.querySelector('input[type="file"]');
      expect(input).toHaveAttribute('accept', 'image/*,.pdf');
    });

    it('calls onChange with file path/url after upload', async () => {
      const onChange = vi.fn();
      mockPost.mockResolvedValue({
        data: { success: true, object: '/uploads/uploaded-file.pdf' },
      });

      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            onChange,
            fileUploadApi: '/fs/upload',
          })}
        />,
      );

      const input = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      const file = createMockFile('my-report.pdf', 2048, 'application/pdf');

      await userEvent.setup().upload(input, file);

      await waitFor(() => {
        expect(onChange).toHaveBeenCalledWith('/uploads/uploaded-file.pdf');
      });
    });

    it('shows progress bar during upload', async () => {
      let resolveUpload!: (value: unknown) => void;
      mockPost.mockImplementation(
        () =>
          new Promise((resolve) => {
            resolveUpload = resolve;
          }),
      );

      const onChange = vi.fn();
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            onChange,
            fileUploadApi: '/fs/upload',
          })}
        />,
      );

      const input = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      const file = createMockFile();

      await userEvent.setup().upload(input, file);

      // Progress bar should appear during upload
      await waitFor(() => {
        expect(screen.getByRole('progressbar')).toBeInTheDocument();
      });

      const progressBar = screen.getByRole('progressbar');
      expect(progressBar).toHaveAttribute('aria-valuenow', '0');
      expect(progressBar).toHaveAttribute('aria-valuemin', '0');
      expect(progressBar).toHaveAttribute('aria-valuemax', '100');

      // Resolve upload to complete the cycle
      resolveUpload({
        data: { success: true, object: '/uploads/document.pdf' },
      });

      await waitFor(() => {
        expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
      });
    });
  });

  // =========================================================================
  // File Upload
  // =========================================================================

  describe('file upload', () => {
    it('triggers upload on file selection', async () => {
      mockPost.mockResolvedValue({
        data: { success: true, object: '/uploads/document.pdf' },
      });

      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            onChange: vi.fn(),
            fileUploadApi: '/fs/upload',
          })}
        />,
      );

      const input = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      const file = createMockFile('test.pdf', 1024, 'application/pdf');

      await userEvent.setup().upload(input, file);

      await waitFor(() => {
        expect(mockPost).toHaveBeenCalledTimes(1);
      });

      // Verify FormData was sent to the correct endpoint
      expect(mockPost).toHaveBeenCalledWith(
        '/fs/upload',
        expect.any(FormData),
        expect.objectContaining({
          headers: expect.objectContaining({
            'Content-Type': 'multipart/form-data',
          }),
        }),
      );
    });

    it('shows upload progress state', async () => {
      let resolveUpload!: (value: unknown) => void;
      mockPost.mockImplementation(
        () =>
          new Promise((resolve) => {
            resolveUpload = resolve;
          }),
      );

      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            onChange: vi.fn(),
            fileUploadApi: '/fs/upload',
          })}
        />,
      );

      const input = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      const file = createMockFile();

      await userEvent.setup().upload(input, file);

      // Upload status indicator should be visible
      await waitFor(() => {
        const statusEl = screen.getByRole('status', {
          name: /uploading file/i,
        });
        expect(statusEl).toBeInTheDocument();
      });

      // File name should appear in the progress area
      expect(screen.getByText('document.pdf')).toBeInTheDocument();

      // Resolve to clean up
      resolveUpload({
        data: { success: true, object: '/uploads/document.pdf' },
      });

      await waitFor(() => {
        expect(
          screen.queryByRole('status', { name: /uploading file/i }),
        ).not.toBeInTheDocument();
      });
    });

    it('handles upload success', async () => {
      const onChange = vi.fn();

      // Use presigned URL flow (no fileUploadApi) to cover both paths
      mockGet.mockResolvedValue({
        data: {
          uploadUrl: 'https://s3.example.com/presigned',
          objectKey: 'files/success-file.pdf',
        },
      });
      mockPut.mockResolvedValue({});

      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            onChange,
          })}
        />,
      );

      const input = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      const file = createMockFile('success.pdf');

      await userEvent.setup().upload(input, file);

      // Presigned URL flow: GET presigned → PUT to S3
      await waitFor(() => {
        expect(mockGet).toHaveBeenCalledTimes(1);
      });

      await waitFor(() => {
        expect(mockPut).toHaveBeenCalledTimes(1);
      });

      // onChange should receive the objectKey from presigned response
      await waitFor(() => {
        expect(onChange).toHaveBeenCalledWith('files/success-file.pdf');
      });

      // No error should be displayed
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });

    it('handles upload error', async () => {
      mockPost.mockRejectedValue(new Error('Network error'));

      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            onChange: vi.fn(),
            fileUploadApi: '/fs/upload',
          })}
        />,
      );

      const input = document.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      const file = createMockFile();

      await userEvent.setup().upload(input, file);

      // Error should be displayed in an alert
      await waitFor(() => {
        const alert = screen.getByRole('alert');
        expect(alert).toBeInTheDocument();
        expect(alert).toHaveTextContent('Network error');
      });
    });

    it('applies accept filter (e.g., "image/*,.pdf")', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            accept: 'image/*,.pdf',
          })}
        />,
      );

      const input = document.querySelector('input[type="file"]');
      expect(input).toHaveAttribute('accept', 'image/*,.pdf');
    });
  });

  // =========================================================================
  // Drag and Drop
  // =========================================================================

  describe('drag and drop', () => {
    it('shows drag-over indicator when file dragged over', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
          })}
        />,
      );

      const dropZone = screen.getByRole('button', {
        name: /drag and drop/i,
      });

      // Initially should have default border color
      expect(dropZone).toHaveClass('border-gray-300');
      expect(dropZone).not.toHaveClass('border-blue-400');

      // Simulate drag over
      fireEvent.dragOver(dropZone);

      // After drag-over: should show blue indicator
      expect(dropZone).toHaveClass('border-blue-400');
      expect(dropZone).toHaveClass('bg-blue-50');

      // Simulate drag leave to reset
      fireEvent.dragLeave(dropZone);

      // Should return to default styling
      expect(dropZone).toHaveClass('border-gray-300');
      expect(dropZone).not.toHaveClass('border-blue-400');
    });

    it('accepts dropped file', async () => {
      const onChange = vi.fn();
      mockPost.mockResolvedValue({
        data: { success: true, object: '/uploads/dropped-file.pdf' },
      });

      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            onChange,
            fileUploadApi: '/fs/upload',
          })}
        />,
      );

      const dropZone = screen.getByRole('button', {
        name: /drag and drop/i,
      });

      const file = createMockFile('dropped.pdf', 2048, 'application/pdf');

      // Simulate drop event with file in dataTransfer
      fireEvent.drop(dropZone, {
        dataTransfer: {
          files: [file],
        },
      });

      // Upload should be triggered via apiClient.post
      await waitFor(() => {
        expect(mockPost).toHaveBeenCalledTimes(1);
      });

      // onChange should be called with the uploaded file path
      await waitFor(() => {
        expect(onChange).toHaveBeenCalledWith('/uploads/dropped-file.pdf');
      });
    });
  });

  // =========================================================================
  // Remove Functionality
  // =========================================================================

  describe('remove functionality', () => {
    it('shows remove button when file exists', () => {
      render(
        <FileField
          {...createDefaultProps({
            value: '/uploads/existing-file.pdf',
            mode: 'edit',
            onChange: vi.fn(),
          })}
        />,
      );

      const removeButton = screen.getByRole('button', {
        name: /remove file existing-file\.pdf/i,
      });
      expect(removeButton).toBeInTheDocument();
    });

    it('calls onChange with null when remove button clicked', async () => {
      const onChange = vi.fn();
      render(
        <FileField
          {...createDefaultProps({
            value: '/uploads/existing-file.pdf',
            mode: 'edit',
            onChange,
          })}
        />,
      );

      const removeButton = screen.getByRole('button', {
        name: /remove file existing-file\.pdf/i,
      });

      await userEvent.setup().click(removeButton);

      expect(onChange).toHaveBeenCalledWith(null);
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      // File input should be enabled
      const input = document.querySelector('input[type="file"]');
      expect(input).not.toBeDisabled();

      // Drop zone should be interactive (tabIndex=0)
      const dropZone = screen.getByRole('button', {
        name: /drag and drop/i,
      });
      expect(dropZone).toHaveAttribute('tabindex', '0');
      expect(dropZone).toHaveAttribute('aria-disabled', 'false');
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            access: 'readonly',
            value: '/uploads/readonly-file.pdf',
          })}
        />,
      );

      // File input should be disabled
      const input = document.querySelector('input[type="file"]');
      expect(input).toBeDisabled();

      // Current file should still be displayed
      expect(screen.getByText('readonly-file.pdf')).toBeInTheDocument();

      // Remove button should NOT be present (readonly hides it)
      expect(
        screen.queryByRole('button', { name: /remove file/i }),
      ).not.toBeInTheDocument();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            access: 'forbidden',
          })}
        />,
      );

      const message = screen.getByText('Access denied');
      expect(message).toBeInTheDocument();
      expect(message).toHaveClass('italic');

      // No file input should be present
      expect(
        document.querySelector('input[type="file"]'),
      ).not.toBeInTheDocument();

      // No drop zone should be present
      expect(
        screen.queryByRole('button', { name: /drag and drop/i }),
      ).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            error: 'File is required',
          })}
        />,
      );

      const errorElement = screen.getByText('File is required');
      expect(errorElement).toBeInTheDocument();
      // The error text is inside a <p> with role="alert"
      expect(errorElement.closest('[role="alert"]')).toBeTruthy();
    });

    it('shows validation errors', () => {
      render(
        <FileField
          {...createDefaultProps({
            mode: 'edit',
            error: 'Invalid file type',
          })}
        />,
      );

      expect(screen.getByText('Invalid file type')).toBeInTheDocument();

      // The file input should have aria-invalid="true"
      const input = document.querySelector('input[type="file"]');
      expect(input).toHaveAttribute('aria-invalid', 'true');

      // Error should be linked via aria-describedby containing error ID
      expect(input).toHaveAttribute(
        'aria-describedby',
        expect.stringContaining('test_file-error'),
      );
    });
  });

  // =========================================================================
  // Null/Empty Handling
  // =========================================================================

  describe('null/empty handling', () => {
    it('handles null value', () => {
      render(
        <FileField
          {...createDefaultProps({
            value: null,
            mode: 'display',
          })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('handles undefined value', () => {
      render(
        <FileField
          {...createDefaultProps({
            value: undefined as unknown as string | null,
            mode: 'display',
          })}
        />,
      );

      // Component treats undefined as falsy → shows emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <FileField
          {...createDefaultProps({
            value: SAMPLE_FILE_PATH,
            mode: 'display',
            isVisible: true,
          })}
        />,
      );

      expect(screen.getByRole('link')).toBeInTheDocument();
      expect(screen.getByText('report.pdf')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <FileField
          {...createDefaultProps({
            value: SAMPLE_FILE_PATH,
            mode: 'display',
            isVisible: false,
          })}
        />,
      );

      // Empty fragment → container should have no visible content
      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
      expect(screen.queryByText('report.pdf')).not.toBeInTheDocument();
    });
  });
});
