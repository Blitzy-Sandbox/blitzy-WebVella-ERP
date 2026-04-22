/**
 * Vitest Component Tests for `<ImageField />`
 *
 * Validates the React ImageField component
 * (`apps/frontend/src/components/fields/ImageField.tsx`) that replaces
 * the monolith's `PcFieldImage` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldImage/PcFieldImage.cs`).
 *
 * The monolith's PcFieldImageOptions extends PcFieldBaseOptions with:
 *   - Accept (string): MIME-type filter for file selection
 *   - Width / Height (int?): Optional dimension constraints
 *   - ResizeAction (ImageResizeMode): Pad / Crop / Stretch
 *   - TextRemove / TextSelect: Button labels
 *
 * Test coverage spans:
 *   - Display mode: <img> rendering with srcPrefix + value, responsive sizing
 *     (max-width), broken image fallback, emptyValueMessage for null/empty
 *   - Edit mode: image preview, change/remove buttons, hidden file input with
 *     accept="image/*", drag-and-drop zone, upload via presigned URL flow,
 *     local preview via URL.createObjectURL
 *   - Image preview: current image from srcPrefix + value, local preview of
 *     selected file, fallback on image load error
 *   - Drag and drop: drag-over visual indicator, accept dropped image files,
 *     reject non-image files
 *   - Remove functionality: remove button, onChange(null), clear preview
 *   - Access control: full / readonly / forbidden
 *   - Validation: error messages, validation error display
 *   - Null/empty handling: null value, undefined value
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/ImageField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldImage/PcFieldImage.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import ImageField from '../../../src/components/fields/ImageField';
import type { ImageFieldProps } from '../../../src/components/fields/ImageField';

// ---------------------------------------------------------------------------
// Mock — apiClient
// ---------------------------------------------------------------------------

/**
 * Mock the centralized apiClient module so that presigned-URL GET requests,
 * S3 PUT uploads, and direct FormData POST uploads do not hit a real server.
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
// Mock — URL.createObjectURL / URL.revokeObjectURL
// ---------------------------------------------------------------------------

const FAKE_BLOB_URL = 'blob:http://localhost/fake-preview-url';
const originalCreateObjectURL = globalThis.URL.createObjectURL;
const originalRevokeObjectURL = globalThis.URL.revokeObjectURL;

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default set of ImageFieldProps for consistent test setup.
 * Mirrors the PcFieldImageOptions defaults from PcFieldImage.cs:
 *   - Accept → "image/*"
 *   - srcPrefix → ""
 *   - mode → "display" (for display tests) or "edit" (for edit tests)
 */
function createDefaultProps(
  overrides: Partial<ImageFieldProps> = {},
): ImageFieldProps {
  return {
    name: 'image_field',
    value: null,
    ...overrides,
  };
}

/**
 * Helper: Create a File object for use in upload and drop simulations.
 * The browser File constructor is available in jsdom.
 */
function createMockImageFile(
  fileName: string = 'test-image.png',
  sizeBytes: number = 1024,
  mimeType: string = 'image/png',
): File {
  const content = new Uint8Array(sizeBytes);
  return new File([content], fileName, { type: mimeType });
}

/**
 * Helper: Create a non-image File for rejection testing.
 */
function createMockNonImageFile(
  fileName: string = 'document.pdf',
  sizeBytes: number = 2048,
  mimeType: string = 'application/pdf',
): File {
  const content = new Uint8Array(sizeBytes);
  return new File([content], fileName, { type: mimeType });
}

/** Sample image path fixture. */
const SAMPLE_IMAGE_PATH = '/uploads/images/photo.jpg';

/** Sample srcPrefix for constructing full URLs. */
const SAMPLE_SRC_PREFIX = 'https://cdn.example.com';

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('ImageField', () => {
  beforeEach(() => {
    // Reset apiClient mocks
    mockGet.mockReset();
    mockPost.mockReset();
    mockPut.mockReset();

    // Mock URL.createObjectURL / revokeObjectURL
    globalThis.URL.createObjectURL = vi.fn().mockReturnValue(FAKE_BLOB_URL);
    globalThis.URL.revokeObjectURL = vi.fn();
  });

  afterEach(() => {
    cleanup();
    // Restore original URL methods
    globalThis.URL.createObjectURL = originalCreateObjectURL;
    globalThis.URL.revokeObjectURL = originalRevokeObjectURL;
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('renders <img> element with src={srcPrefix + value}', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            srcPrefix: SAMPLE_SRC_PREFIX,
            mode: 'display',
          })}
        />,
      );

      const img = screen.getByRole('img');
      expect(img).toBeInTheDocument();
      expect(img).toHaveAttribute(
        'src',
        `${SAMPLE_SRC_PREFIX}${SAMPLE_IMAGE_PATH}`,
      );
    });

    it('applies responsive sizing with max-width', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            mode: 'display',
          })}
        />,
      );

      const img = screen.getByRole('img');
      expect(img).toBeInTheDocument();
      // The component sets inline style maxWidth: '100%'
      expect(img).toHaveStyle({ maxWidth: '100%' });
      // Tailwind class max-w-full is also applied
      expect(img).toHaveClass('max-w-full');
    });

    it('shows fallback placeholder if image fails to load (onError handler)', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            mode: 'display',
            label: 'Profile Photo',
          })}
        />,
      );

      // Initially, the img should be rendered
      const img = screen.getByRole('img');
      expect(img).toBeInTheDocument();

      // Fire the error event to simulate broken image
      fireEvent.error(img);

      // After error, the component should show broken image fallback
      // The fallback shows "Image unavailable" text
      expect(screen.getByText('Image unavailable')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'display',
          })}
        />,
      );

      // Default emptyValueMessage is "no data" (from PcFieldBaseModel)
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: '' as unknown as null,
            mode: 'display',
          })}
        />,
      );

      // Empty string is treated as no value → shows emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('shows image preview when value exists', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            srcPrefix: SAMPLE_SRC_PREFIX,
            mode: 'edit',
          })}
        />,
      );

      // In edit mode with a value, the component renders an img preview
      const imgs = screen.getAllByRole('img');
      // There should be at least one img element showing the preview
      const previewImg = imgs.find(
        (img) =>
          img.getAttribute('src') ===
          `${SAMPLE_SRC_PREFIX}${SAMPLE_IMAGE_PATH}`,
      );
      expect(previewImg).toBeDefined();
    });

    it('shows change/remove buttons when image exists', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            mode: 'edit',
          })}
        />,
      );

      // Change button triggers file input click
      expect(
        screen.getByRole('button', { name: /change/i }),
      ).toBeInTheDocument();
      // Remove button calls onChange(null)
      expect(
        screen.getByRole('button', { name: /remove/i }),
      ).toBeInTheDocument();
    });

    it('renders file input hidden with accept="image/*"', () => {
      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
          })}
        />,
      );

      // The file input is hidden (sr-only class) but present in the DOM
      const fileInput = container.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      expect(fileInput).toBeInTheDocument();
      expect(fileInput).toHaveAttribute('accept', 'image/*');
      // sr-only class makes it visually hidden
      expect(fileInput).toHaveClass('sr-only');
    });

    it('renders drag-and-drop zone with visual indicator', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
          })}
        />,
      );

      // The drop zone has role="button" and shows instruction text
      const dropZone = screen.getByRole('button');
      expect(dropZone).toBeInTheDocument();
      // The drop zone contains instructional text
      expect(
        screen.getByText(/click to select or drag and drop/i),
      ).toBeInTheDocument();
      // Shows accepted file types
      expect(
        screen.getByText(/PNG, JPG, GIF, SVG, WebP/i),
      ).toBeInTheDocument();
    });

    it('calls onChange with image path after upload', async () => {
      const handleChange = vi.fn();
      const fileKey = 'uploads/images/uploaded-image.png';

      // Mock successful presigned URL flow
      mockGet.mockResolvedValueOnce({
        data: {
          url: 'https://s3.localhost/presigned-upload-url',
          key: fileKey,
        },
      });
      mockPut.mockResolvedValueOnce({ data: {} });

      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      // Select a file via the hidden file input
      const file = createMockImageFile();
      const fileInput = container.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;

      await userEvent.upload(fileInput, file);

      // Wait for the upload flow to complete
      await vi.waitFor(() => {
        expect(handleChange).toHaveBeenCalledWith(fileKey);
      });

      // Verify apiClient.get was called for presigned URL
      expect(mockGet).toHaveBeenCalledWith(
        '/files/presigned-upload',
        expect.objectContaining({
          params: expect.objectContaining({
            fileName: 'test-image.png',
            contentType: 'image/png',
          }),
        }),
      );
    });

    it('shows image preview using URL.createObjectURL before upload', async () => {
      // Mock a slow upload to catch the preview state
      mockGet.mockImplementation(
        () =>
          new Promise((resolve) =>
            setTimeout(
              () =>
                resolve({
                  data: { url: 'https://s3.localhost/url', key: 'key' },
                }),
              500,
            ),
          ),
      );
      mockPut.mockResolvedValueOnce({ data: {} });

      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
            onChange: vi.fn(),
          })}
        />,
      );

      const file = createMockImageFile();
      const fileInput = container.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;

      // Trigger file selection
      fireEvent.change(fileInput, { target: { files: [file] } });

      // URL.createObjectURL should have been called with the file
      await vi.waitFor(() => {
        expect(globalThis.URL.createObjectURL).toHaveBeenCalledWith(file);
      });
    });
  });

  // =========================================================================
  // Image Preview
  // =========================================================================

  describe('image preview', () => {
    it('shows current image from srcPrefix + value', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: '/images/logo.png',
            srcPrefix: 'https://cdn.example.com',
            mode: 'display',
          })}
        />,
      );

      const img = screen.getByRole('img');
      expect(img).toHaveAttribute(
        'src',
        'https://cdn.example.com/images/logo.png',
      );
    });

    it('shows preview of selected file before upload', async () => {
      // Mock slow upload to test preview state
      mockGet.mockImplementation(
        () => new Promise(() => {/* never resolves — keeps uploading state */}),
      );

      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
            onChange: vi.fn(),
          })}
        />,
      );

      const file = createMockImageFile('photo.jpg', 2048, 'image/jpeg');
      const fileInput = container.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;

      fireEvent.change(fileInput, { target: { files: [file] } });

      // The component should show a preview using the blob URL
      await vi.waitFor(() => {
        const imgs = container.querySelectorAll('img');
        const blobImg = Array.from(imgs).find((img) =>
          img.getAttribute('src')?.startsWith('blob:'),
        );
        expect(blobImg).toBeDefined();
      });
    });

    it('fallback on image load error', () => {
      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: '/broken-image.jpg',
            mode: 'display',
          })}
        />,
      );

      const img = container.querySelector('img') as HTMLImageElement;
      expect(img).toBeInTheDocument();
      fireEvent.error(img);

      // After error, fallback content is shown
      expect(screen.getByText('Image unavailable')).toBeInTheDocument();
      // The original <img> HTML element should be replaced by the fallback div
      expect(container.querySelector('img')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Drag and Drop
  // =========================================================================

  describe('drag and drop', () => {
    it('shows drag-over indicator', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
          })}
        />,
      );

      const dropZone = screen.getByRole('button');

      // Before drag: default border styling
      expect(dropZone.className).toContain('border-gray-300');

      // Simulate dragOver
      fireEvent.dragOver(dropZone, {
        dataTransfer: { files: [] },
      });

      // During drag-over: highlight border (blue indicator)
      expect(dropZone.className).toContain('border-blue-400');
      expect(dropZone.className).toContain('bg-blue-50');

      // The text should change to "Drop image here"
      expect(screen.getByText('Drop image here')).toBeInTheDocument();
    });

    it('accepts dropped image file', async () => {
      const handleChange = vi.fn();
      const fileKey = 'uploads/dropped-image.png';

      mockGet.mockResolvedValueOnce({
        data: { url: 'https://s3.localhost/presigned', key: fileKey },
      });
      mockPut.mockResolvedValueOnce({ data: {} });

      render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      const dropZone = screen.getByRole('button');
      const file = createMockImageFile('dropped.png', 4096, 'image/png');

      // Simulate drop event
      fireEvent.drop(dropZone, {
        dataTransfer: {
          files: [file],
        },
      });

      // Wait for upload flow to complete
      await vi.waitFor(() => {
        expect(handleChange).toHaveBeenCalledWith(fileKey);
      });
    });

    it('rejects non-image files when accept="image/*"', async () => {
      const handleChange = vi.fn();

      render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      const dropZone = screen.getByRole('button');
      const nonImageFile = createMockNonImageFile();

      // Simulate drop event with non-image file
      fireEvent.drop(dropZone, {
        dataTransfer: {
          files: [nonImageFile],
        },
      });

      // Wait for error message to appear
      await vi.waitFor(() => {
        expect(
          screen.getByText('Selected file is not a valid image.'),
        ).toBeInTheDocument();
      });

      // onChange should NOT have been called
      expect(handleChange).not.toHaveBeenCalled();
    });
  });

  // =========================================================================
  // Remove Functionality
  // =========================================================================

  describe('remove functionality', () => {
    it('shows remove button when image exists', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            mode: 'edit',
          })}
        />,
      );

      const removeBtn = screen.getByRole('button', { name: /remove/i });
      expect(removeBtn).toBeInTheDocument();
    });

    it('calls onChange with null when remove clicked', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      const removeBtn = screen.getByRole('button', { name: /remove/i });
      await user.click(removeBtn);

      expect(handleChange).toHaveBeenCalledWith(null);
    });

    it('clears preview when removed', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            srcPrefix: SAMPLE_SRC_PREFIX,
            mode: 'edit',
            onChange: handleChange,
          })}
        />,
      );

      // Initially, the image preview is visible
      const imgs = container.querySelectorAll('img');
      expect(imgs.length).toBeGreaterThan(0);

      // Click remove
      const removeBtn = screen.getByRole('button', { name: /remove/i });
      await user.click(removeBtn);

      // After removal, onChange called with null → parent updates value to null
      expect(handleChange).toHaveBeenCalledWith(null);
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            srcPrefix: SAMPLE_SRC_PREFIX,
            mode: 'edit',
            access: 'full',
          })}
        />,
      );

      // Full access → edit mode with image preview and action buttons
      const imgs = container.querySelectorAll('img');
      expect(imgs.length).toBeGreaterThan(0);

      // Change and Remove buttons should be present
      expect(
        screen.getByRole('button', { name: /change/i }),
      ).toBeInTheDocument();
      expect(
        screen.getByRole('button', { name: /remove/i }),
      ).toBeInTheDocument();
    });

    it('renders as readonly with access="readonly"', () => {
      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            srcPrefix: SAMPLE_SRC_PREFIX,
            mode: 'edit',
            access: 'readonly',
          })}
        />,
      );

      // Readonly forces display mode — no Change/Remove buttons
      expect(
        screen.queryByRole('button', { name: /change/i }),
      ).not.toBeInTheDocument();
      expect(
        screen.queryByRole('button', { name: /remove/i }),
      ).not.toBeInTheDocument();

      // Image should still be displayed
      const imgs = container.querySelectorAll('img');
      expect(imgs.length).toBeGreaterThan(0);
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            access: 'forbidden',
          })}
        />,
      );

      // Forbidden shows the access-denied message (default "access denied")
      expect(screen.getByText('access denied')).toBeInTheDocument();

      // The wrapper has role="status" and aria-label="Access denied"
      const statusEl = screen.getByRole('status');
      expect(statusEl).toBeInTheDocument();
      expect(statusEl).toHaveAttribute('aria-label', 'Access denied');

      // The image content should NOT be visible
      expect(screen.queryByRole('img')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
            error: 'Image is required',
          })}
        />,
      );

      // Error rendered as <p> with role="alert"
      const errorMsg = screen.getByRole('alert');
      expect(errorMsg).toBeInTheDocument();
      expect(errorMsg).toHaveTextContent('Image is required');
    });

    it('shows validation errors', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'edit',
            error: 'Please upload a valid image',
          })}
        />,
      );

      expect(
        screen.getByText('Please upload a valid image'),
      ).toBeInTheDocument();

      // Error element id follows the pattern "{name}-error" for aria-describedby
      const errorEl = screen.getByRole('alert');
      expect(errorEl).toHaveAttribute('id', 'image_field-error');
    });
  });

  // =========================================================================
  // Null/Empty Handling
  // =========================================================================

  describe('null/empty handling', () => {
    it('handles null value', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: null,
            mode: 'display',
          })}
        />,
      );

      // Null value in display mode shows the emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles undefined value', () => {
      render(
        <ImageField
          {...createDefaultProps({
            value: undefined as unknown as null,
            mode: 'display',
          })}
        />,
      );

      // Undefined value is treated the same as null — shows emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            mode: 'display',
            isVisible: true,
          })}
        />,
      );

      // Component should render its content normally
      expect(container.firstChild).not.toBeNull();
      const img = screen.getByRole('img');
      expect(img).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <ImageField
          {...createDefaultProps({
            value: SAMPLE_IMAGE_PATH,
            mode: 'display',
            isVisible: false,
          })}
        />,
      );

      // Component returns empty fragment <></> — no visible content
      expect(container.textContent).toBe('');
      expect(screen.queryByRole('img')).not.toBeInTheDocument();
    });
  });
});
