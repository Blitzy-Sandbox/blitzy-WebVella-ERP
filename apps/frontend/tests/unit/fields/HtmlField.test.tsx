/**
 * Vitest Component Tests for `<HtmlField />`
 *
 * Validates the React HtmlField component
 * (`apps/frontend/src/components/fields/HtmlField.tsx`) that replaces
 * the monolith's `PcFieldHtml` ViewComponent
 * (`WebVella.Erp.Web/Components/PcFieldHtml/PcFieldHtml.cs`).
 *
 * The monolith's PcFieldHtmlOptions extend PcFieldBaseOptions with:
 *   - UploadMode (HtmlUploadMode enum: None, Base64, Url) — default None
 *   - ToolbarMode (HtmlToolbarMode enum: Basic, Standard, Full) — default Basic
 *
 * Test coverage spans:
 *   - Display mode: dangerouslySetInnerHTML rendering, XSS sanitisation,
 *     prose-like Tailwind typography styling, emptyValueMessage for null/empty
 *   - Edit mode: contentEditable div with role="textbox", current content display,
 *     onChange callback on input events, toolbar rendering
 *   - Toolbar modes: basic (Bold, Italic, Underline, Link), standard (+Lists,
 *     Headings, Quote, Image), full (+Table, Code, Alignment, Undo/Redo)
 *   - Upload modes: none (no image button), base64 (hidden file input),
 *     url (image button present), default none
 *   - XSS prevention: script tag stripping, event handler attribute removal,
 *     safe HTML element preservation
 *   - Access control: full / readonly / forbidden
 *   - Validation: error messages, validation error display
 *   - Null/empty handling: null and empty-string values
 *   - Visibility: isVisible true/false
 *
 * @see apps/frontend/src/components/fields/HtmlField.tsx
 * @see WebVella.Erp.Web/Components/PcFieldHtml/PcFieldHtml.cs
 * @see WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import HtmlField from '../../../src/components/fields/HtmlField';
import type {
  HtmlFieldProps,
  HtmlUploadMode,
  HtmlToolbarMode,
} from '../../../src/components/fields/HtmlField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Creates a complete default HtmlFieldProps for consistent test setup.
 * Mirrors the PcFieldHtmlOptions defaults from PcFieldHtml.cs:
 *   - UploadMode = None → 'none'
 *   - ToolbarMode = Basic → 'basic'
 */
function createDefaultProps(
  overrides: Partial<HtmlFieldProps> = {},
): HtmlFieldProps {
  return {
    name: 'html_field',
    value: null,
    ...overrides,
  };
}

/** Sample HTML fixture with basic formatting (paragraph + strong). */
const SAMPLE_HTML = '<p>Hello <strong>world</strong></p>';

/** Rich HTML fixture with headings, links, and emphasis. */
const RICH_HTML =
  '<h1>Title</h1><p>Paragraph with <em>emphasis</em> and ' +
  '<a href="https://example.com">link</a></p>';

/** XSS payload fixture: script tag injection. */
const XSS_SCRIPT_HTML =
  '<script>alert("xss")</script><p>Safe content</p>';

/** XSS payload fixture: event handler attribute injection. */
const XSS_EVENT_HTML =
  '<p onclick="alert(\'xss\')" onmouseover="steal()">Click me</p>';

/** HTML fixture containing only safe, allowlisted elements and attributes. */
const SAFE_HTML =
  '<p>paragraph</p><h1>heading</h1><strong>bold</strong><em>italic</em>' +
  '<a href="https://test.com">link</a><img src="test.jpg" alt="test image">';

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('HtmlField', () => {
  beforeEach(() => {
    // jsdom v28 does not implement document.execCommand (deprecated API).
    // The HtmlField component uses it for toolbar formatting commands.
    // Mock it to prevent "document.execCommand is not a function" errors.
    if (typeof document !== 'undefined' && !document.execCommand) {
      (document as any).execCommand = vi.fn().mockReturnValue(true);
    }
  });

  afterEach(() => {
    cleanup();
  });

  // =========================================================================
  // Display Mode
  // =========================================================================

  describe('display mode', () => {
    it('renders HTML content using dangerouslySetInnerHTML', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({ value: SAMPLE_HTML, mode: 'display' }) as any)}
        />,
      );

      // Rendered output should contain the actual HTML elements, not escaped text
      expect(screen.getByText('world')).toBeInTheDocument();
      const strong = container.querySelector('strong');
      expect(strong).toBeInTheDocument();
      expect(strong).toHaveTextContent('world');
    });

    it('sanitizes HTML content to prevent XSS', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({
            value: XSS_SCRIPT_HTML,
            mode: 'display',
          }) as any)}
        />,
      );

      // DOMPurify should strip the <script> tag
      expect(container.querySelector('script')).not.toBeInTheDocument();
      // Safe paragraph content should be preserved
      expect(screen.getByText('Safe content')).toBeInTheDocument();
    });

    it('applies prose-like Tailwind typography styling', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({ value: RICH_HTML, mode: 'display' }) as any)}
        />,
      );

      // The display wrapper div uses Tailwind child-selector utility classes
      // for prose-like typography (e.g. [&_h1]:text-2xl, [&_p]:mb-2).
      // Note: jsdom's querySelector does not handle brackets inside CSS
      // attribute selectors, so we locate the div via its rendered child
      // content (the <h1> from RICH_HTML) and then inspect the parent's
      // className programmatically.
      const heading = container.querySelector('h1');
      expect(heading).toBeInTheDocument();
      const displayDiv = heading!.parentElement!;
      expect(displayDiv.className).toContain('[&_h1]');
      expect(displayDiv.className).toContain('[&_p]');
      expect(displayDiv.className).toContain('[&_a]');
      expect(displayDiv.className).toContain('[&_ul]');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <HtmlField
          {...(createDefaultProps({ value: null, mode: 'display' }) as any)}
        />,
      );

      // Default emptyValueMessage is "no data" (from PcFieldBaseModel)
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is empty string', () => {
      render(
        <HtmlField
          {...(createDefaultProps({ value: '', mode: 'display' }) as any)}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Edit Mode
  // =========================================================================

  describe('edit mode', () => {
    it('renders a rich text editor (contentEditable div or textarea)', () => {
      render(
        <HtmlField
          {...(createDefaultProps({ value: '', mode: 'edit' }) as any)}
        />,
      );

      // The editor uses a contentEditable div with role="textbox"
      const textbox = screen.getByRole('textbox');
      expect(textbox).toBeInTheDocument();
      expect(textbox).toHaveAttribute('contenteditable', 'true');
      expect(textbox).toHaveAttribute('aria-multiline', 'true');
    });

    it('displays current HTML content', () => {
      render(
        <HtmlField
          {...(createDefaultProps({ value: SAMPLE_HTML, mode: 'edit' }) as any)}
        />,
      );

      const textbox = screen.getByRole('textbox');
      // The contentEditable div renders sanitized HTML via dangerouslySetInnerHTML
      expect(textbox.innerHTML).toContain('<p>');
      expect(textbox.innerHTML).toContain('Hello');
      expect(textbox.innerHTML).toContain('<strong>world</strong>');
    });

    it('calls onChange with HTML string when content changes', () => {
      const handleChange = vi.fn();
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            onChange: handleChange,
          }) as any)}
        />,
      );

      const textbox = screen.getByRole('textbox');
      // Simulate content editing: set innerHTML and fire input event.
      // The component's handleInput reads editorRef.current.innerHTML
      // and passes sanitized HTML to onChange.
      textbox.innerHTML = '<p>New content</p>';
      fireEvent.input(textbox);

      expect(handleChange).toHaveBeenCalled();
      expect(handleChange).toHaveBeenCalledWith(
        expect.stringContaining('New content'),
      );
    });

    it('renders toolbar with buttons', async () => {
      const user = userEvent.setup();

      render(
        <HtmlField
          {...(createDefaultProps({ value: '', mode: 'edit' }) as any)}
        />,
      );

      // Toolbar has role="toolbar" and descriptive aria-label
      const toolbar = screen.getByRole('toolbar');
      expect(toolbar).toBeInTheDocument();
      expect(toolbar).toHaveAttribute('aria-label', 'Text formatting');

      // At minimum the basic toolbar (4 buttons) should be present
      const buttons = within(toolbar).getAllByRole('button');
      expect(buttons.length).toBeGreaterThanOrEqual(4);

      // Verify toolbar buttons are interactive (user-event click)
      const boldButton = within(toolbar).getByLabelText('Bold');
      await user.click(boldButton);
      // No assertion on formatting result — verifying no runtime error
    });
  });

  // =========================================================================
  // Toolbar Modes
  // =========================================================================

  describe('toolbar modes', () => {
    it('renders basic toolbar (Bold, Italic, Underline, Link) for toolbarMode="basic"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            toolbarMode: 'basic' as HtmlToolbarMode,
          }) as any)}
        />,
      );

      const toolbar = screen.getByRole('toolbar');
      const buttons = within(toolbar).getAllByRole('button');

      // Basic mode: Bold, Italic, Underline, Insert Link = 4 buttons
      expect(buttons).toHaveLength(4);

      expect(within(toolbar).getByLabelText('Bold')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Italic')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Underline')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Insert Link')).toBeInTheDocument();
    });

    it('renders standard toolbar (+ Lists, Headings, Quote, Image) for toolbarMode="standard"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            toolbarMode: 'standard' as HtmlToolbarMode,
            uploadMode: 'url' as HtmlUploadMode, // enable image button
          }) as any)}
        />,
      );

      const toolbar = screen.getByRole('toolbar');
      const buttons = within(toolbar).getAllByRole('button');

      // Standard mode: basic (4) + Bullet List, Numbered List, Heading,
      // Blockquote, Insert Image = 9 buttons total
      expect(buttons).toHaveLength(9);

      // Standard-specific buttons
      expect(within(toolbar).getByLabelText('Bullet List')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Numbered List')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Heading')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Blockquote')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Insert Image')).toBeInTheDocument();
    });

    it('renders full toolbar (+ Table, Code, Font, Color, Alignment) for toolbarMode="full"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            toolbarMode: 'full' as HtmlToolbarMode,
            uploadMode: 'base64' as HtmlUploadMode, // enable image button
          }) as any)}
        />,
      );

      const toolbar = screen.getByRole('toolbar');
      const buttons = within(toolbar).getAllByRole('button');

      // Full mode: basic (4) + standard (5 incl image) + Code Block,
      // Align Left/Center/Right, Insert Table, Undo, Redo = 16 buttons
      expect(buttons).toHaveLength(16);

      // Full-specific buttons
      expect(within(toolbar).getByLabelText('Code Block')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Align Left')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Align Center')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Align Right')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Insert Table')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Undo')).toBeInTheDocument();
      expect(within(toolbar).getByLabelText('Redo')).toBeInTheDocument();
    });

    it('defaults to "basic" toolbar when toolbarMode not provided', () => {
      render(
        <HtmlField
          {...(createDefaultProps({ value: '', mode: 'edit' }) as any)}
        />,
      );

      const toolbar = screen.getByRole('toolbar');
      const buttons = within(toolbar).getAllByRole('button');

      // Default toolbarMode is 'basic' → 4 buttons
      expect(buttons).toHaveLength(4);
    });
  });

  // =========================================================================
  // Upload Modes
  // =========================================================================

  describe('upload modes', () => {
    it('has no image upload when uploadMode="none"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            toolbarMode: 'standard' as HtmlToolbarMode,
            uploadMode: 'none' as HtmlUploadMode,
          }) as any)}
        />,
      );

      const toolbar = screen.getByRole('toolbar');
      // When uploadMode is 'none', the Image button is not rendered
      expect(
        within(toolbar).queryByLabelText('Insert Image'),
      ).not.toBeInTheDocument();
    });

    it('supports Base64 image embed when uploadMode="base64"', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            toolbarMode: 'standard' as HtmlToolbarMode,
            uploadMode: 'base64' as HtmlUploadMode,
          }) as any)}
        />,
      );

      const toolbar = screen.getByRole('toolbar');
      // Image button should be present
      expect(within(toolbar).getByLabelText('Insert Image')).toBeInTheDocument();

      // A hidden file input with accept="image/*" enables Base64 upload
      const fileInput = container.querySelector('input[type="file"]');
      expect(fileInput).toBeInTheDocument();
      expect(fileInput).toHaveAttribute('accept', 'image/*');
    });

    it('supports URL image reference when uploadMode="url"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            toolbarMode: 'standard' as HtmlToolbarMode,
            uploadMode: 'url' as HtmlUploadMode,
          }) as any)}
        />,
      );

      const toolbar = screen.getByRole('toolbar');
      // Image button should be present for URL-based image insertion
      expect(within(toolbar).getByLabelText('Insert Image')).toBeInTheDocument();
    });

    it('defaults to uploadMode="none"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            toolbarMode: 'standard' as HtmlToolbarMode,
            // No uploadMode prop → defaults to 'none'
          }) as any)}
        />,
      );

      const toolbar = screen.getByRole('toolbar');
      // Default 'none' hides the image button
      expect(
        within(toolbar).queryByLabelText('Insert Image'),
      ).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // XSS Prevention
  // =========================================================================

  describe('XSS prevention', () => {
    it('sanitizes script tags from HTML content', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({
            value: '<script>alert("xss")</script><p>Safe</p>',
            mode: 'display',
          }) as any)}
        />,
      );

      // DOMPurify strips <script> entirely — not in ALLOWED_TAGS
      expect(container.querySelector('script')).not.toBeInTheDocument();
      // Safe HTML preserved
      expect(screen.getByText('Safe')).toBeInTheDocument();
    });

    it('sanitizes event handlers (onclick, etc.) from HTML', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({
            value: XSS_EVENT_HTML,
            mode: 'display',
          }) as any)}
        />,
      );

      // The paragraph text should render
      expect(screen.getByText('Click me')).toBeInTheDocument();

      // Event handler attributes are stripped — not in ALLOWED_ATTR
      const pElement = container.querySelector('p');
      expect(pElement).toBeInTheDocument();
      expect(pElement).not.toHaveAttribute('onclick');
      expect(pElement).not.toHaveAttribute('onmouseover');
    });

    it('preserves safe HTML (p, h1, strong, em, a, img, etc.)', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({
            value: SAFE_HTML,
            mode: 'display',
          }) as any)}
        />,
      );

      // All ALLOWED_TAGS elements should be preserved in the DOM
      expect(container.querySelector('p')).toBeInTheDocument();
      expect(container.querySelector('h1')).toBeInTheDocument();
      expect(container.querySelector('strong')).toBeInTheDocument();
      expect(container.querySelector('em')).toBeInTheDocument();
      expect(container.querySelector('a')).toBeInTheDocument();
      expect(container.querySelector('a')).toHaveAttribute(
        'href',
        'https://test.com',
      );
      expect(container.querySelector('img')).toBeInTheDocument();
      expect(container.querySelector('img')).toHaveAttribute(
        'alt',
        'test image',
      );
    });
  });

  // =========================================================================
  // Access Control
  // =========================================================================

  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: SAMPLE_HTML,
            mode: 'edit',
            access: 'full',
          }) as any)}
        />,
      );

      // Full access → edit mode with contentEditable enabled
      const textbox = screen.getByRole('textbox');
      expect(textbox).toBeInTheDocument();
      expect(textbox).toHaveAttribute('contenteditable', 'true');

      // Toolbar should be present
      expect(screen.getByRole('toolbar')).toBeInTheDocument();
    });

    it('renders as readonly with access="readonly"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: SAMPLE_HTML,
            mode: 'edit', // mode prop is 'edit' but access overrides to 'display'
            access: 'readonly',
          }) as any)}
        />,
      );

      // Readonly forces display mode — no contentEditable textbox
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      // No toolbar in display mode
      expect(screen.queryByRole('toolbar')).not.toBeInTheDocument();
      // Content should still be visible in display mode
      expect(screen.getByText('world')).toBeInTheDocument();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: SAMPLE_HTML,
            access: 'forbidden',
          }) as any)}
        />,
      );

      // Forbidden shows the access-denied message (default "access denied")
      expect(screen.getByText('access denied')).toBeInTheDocument();

      // The wrapper has role="status" and aria-label="Access denied"
      const statusEl = screen.getByRole('status');
      expect(statusEl).toBeInTheDocument();
      expect(statusEl).toHaveAttribute('aria-label', 'Access denied');

      // The field content should NOT be visible
      expect(screen.queryByText('world')).not.toBeInTheDocument();
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      expect(screen.queryByRole('toolbar')).not.toBeInTheDocument();
    });
  });

  // =========================================================================
  // Validation
  // =========================================================================

  describe('validation', () => {
    it('shows error message when error prop provided', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '',
            mode: 'edit',
            error: 'This field is required',
          }) as any)}
        />,
      );

      // Error rendered as <p> with role="alert"
      const errorMsg = screen.getByRole('alert');
      expect(errorMsg).toBeInTheDocument();
      expect(errorMsg).toHaveTextContent('This field is required');
      expect(errorMsg).toHaveClass('text-red-600');
    });

    it('shows validation errors', () => {
      render(
        <HtmlField
          {...(createDefaultProps({
            value: '<div>bad</div>',
            mode: 'edit',
            error: 'Invalid HTML content',
          }) as any)}
        />,
      );

      expect(screen.getByText('Invalid HTML content')).toBeInTheDocument();

      // Error element id follows the pattern "{name}-error" for aria-describedby
      const errorEl = screen.getByRole('alert');
      expect(errorEl).toHaveAttribute('id', 'html_field-error');

      // The editor should have aria-invalid="true"
      const textbox = screen.getByRole('textbox');
      expect(textbox).toHaveAttribute('aria-invalid', 'true');
    });
  });

  // =========================================================================
  // Null/Empty Handling
  // =========================================================================

  describe('null/empty handling', () => {
    it('handles null value', () => {
      render(
        <HtmlField
          {...(createDefaultProps({ value: null, mode: 'display' }) as any)}
        />,
      );

      // Null value in display mode shows the emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles empty string value', () => {
      render(
        <HtmlField
          {...(createDefaultProps({ value: '', mode: 'display' }) as any)}
        />,
      );

      // Empty string in display mode also shows emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // Visibility
  // =========================================================================

  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({
            value: SAMPLE_HTML,
            mode: 'display',
            isVisible: true,
          }) as any)}
        />,
      );

      // Component should render its content normally
      expect(container.firstChild).not.toBeNull();
      expect(screen.getByText('world')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <HtmlField
          {...(createDefaultProps({
            value: SAMPLE_HTML,
            mode: 'display',
            isVisible: false,
          }) as any)}
        />,
      );

      // Component returns empty fragment <></> — no visible content
      expect(container.textContent).toBe('');
      expect(screen.queryByText('world')).not.toBeInTheDocument();
    });
  });
});
