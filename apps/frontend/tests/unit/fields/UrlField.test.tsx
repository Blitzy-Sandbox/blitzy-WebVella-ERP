/**
 * UrlField — Vitest Unit Tests
 *
 * Comprehensive test suite for the UrlField component that replaces the
 * monolith's PcFieldUrl ViewComponent (WebVella.Erp.Web/Components/PcFieldUrl).
 *
 * Tests cover:
 *   - Display mode (anchor link rendering, truncation, icon, empty state)
 *   - Edit mode (input[type="url"], onChange, maxLength)
 *   - URL validation (native browser validation via type="url")
 *   - New window behavior (openTargetInNewWindow default/true/false)
 *   - Access control (full / readonly / forbidden)
 *   - Validation / error state (aria-invalid, error classes)
 *   - Null / empty value handling
 *   - Visibility toggling (isVisible prop)
 *
 * @module tests/unit/fields/UrlField.test
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom/vitest';
import React from 'react';
import UrlField from '../../../src/components/fields/UrlField';
import type { UrlFieldProps } from '../../../src/components/fields/UrlField';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Builds a complete UrlFieldProps object with sensible defaults.
 * Individual tests override only the props they care about, keeping
 * each test focused and reducing boilerplate.
 */
const buildProps = (overrides: Partial<UrlFieldProps> = {}): UrlFieldProps => ({
  name: 'website',
  value: 'https://example.com',
  ...overrides,
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('UrlField', () => {
  afterEach(() => {
    cleanup();
  });

  // ========================================================================
  // Display Mode
  // ========================================================================
  describe('display mode', () => {
    it('renders URL as <a> link', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toBeInTheDocument();
      expect(link).toHaveAttribute('href', 'https://example.com');
    });

    it('opens in new window when openTargetInNewWindow=true (target="_blank")', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: true,
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toHaveAttribute('target', '_blank');
    });

    it('opens in same window when openTargetInNewWindow=false', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: false,
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toHaveAttribute('target', '_self');
    });

    it('adds rel="noopener noreferrer" for security on external links', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://external-site.org',
            openTargetInNewWindow: true,
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    });

    it('does not add rel attribute when openTargetInNewWindow is false', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: false,
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).not.toHaveAttribute('rel');
    });

    it('shows external link icon when openTargetInNewWindow=true', () => {
      const { container } = render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: true,
          })}
        />,
      );

      // The ExternalLinkIcon renders an <svg> with aria-hidden="true"
      const svg = container.querySelector('svg[aria-hidden="true"]');
      expect(svg).toBeInTheDocument();
    });

    it('does not show external link icon when openTargetInNewWindow=false', () => {
      const { container } = render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: false,
          })}
        />,
      );

      const svg = container.querySelector('svg');
      expect(svg).not.toBeInTheDocument();
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: null,
          })}
        />,
      );

      // Default emptyValueMessage is "no data"
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders custom emptyValueMessage when value is null', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'No URL provided',
          })}
        />,
      );

      expect(screen.getByText('No URL provided')).toBeInTheDocument();
    });

    it('displays truncated URL text for long URLs', () => {
      // URL whose protocol-stripped form exceeds 50 characters
      const longUrl =
        'https://very-long-example-website.com/path/to/some/very/deeply/nested/page';
      // After stripping protocol: "very-long-example-website.com/path/to/some/very/deeply/nested/page" (66 chars)
      // Truncated to 50 chars + ellipsis: "very-long-example-website.com/path/to/some/very/de…"

      render(
        <UrlField
          {...buildProps({ mode: 'display', value: longUrl })}
        />,
      );

      const link = screen.getByRole('link');
      const displayText = link.textContent ?? '';

      // The truncated text should end with ellipsis and be 51 chars (50 + "…")
      expect(displayText).toContain('…');
      expect(displayText.length).toBeLessThanOrEqual(51);

      // Full URL should NOT appear as display text
      expect(displayText).not.toContain('nested/page');
    });

    it('renders short URLs without truncation', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'display', value: 'https://short.io' })}
        />,
      );

      const link = screen.getByRole('link');
      // After stripping "https://", display text is "short.io" (8 chars < 50)
      expect(link).toHaveTextContent('short.io');
      expect(link.textContent).not.toContain('…');
    });

    it('strips protocol from display text', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'display', value: 'https://example.com/page' })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toHaveTextContent('example.com/page');
      expect(link.textContent).not.toContain('https://');
    });

    it('sets title attribute to the full original URL', () => {
      const url = 'https://example.com/long/path';
      render(
        <UrlField {...buildProps({ mode: 'display', value: url })} />,
      );

      const link = screen.getByRole('link');
      expect(link).toHaveAttribute('title', url);
    });

    it('ensures protocol on href even when value lacks it', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'display', value: 'example.com/page' })}
        />,
      );

      const link = screen.getByRole('link');
      // ensureProtocol prepends https://
      expect(link).toHaveAttribute('href', 'https://example.com/page');
    });

    it('sets data-field-name attribute to the field name', () => {
      const { container } = render(
        <UrlField
          {...buildProps({ mode: 'display', value: 'https://example.com', name: 'url_field' })}
        />,
      );

      const wrapper = container.querySelector('[data-field-name="url_field"]');
      expect(wrapper).toBeInTheDocument();
    });
  });

  // ========================================================================
  // Edit Mode
  // ========================================================================
  describe('edit mode', () => {
    it('renders an input[type="url"] in edit mode', () => {
      render(
        <UrlField {...buildProps({ mode: 'edit', value: 'https://example.com' })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      expect(input).toHaveAttribute('type', 'url');
    });

    it('displays current URL value in the input', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: 'https://example.com/path' })}
        />,
      );

      const input = screen.getByRole('textbox') as HTMLInputElement;
      expect(input.value).toBe('https://example.com/path');
    });

    it('calls onChange when user types', async () => {
      const handleChange = vi.fn();
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      fireEvent.change(input, { target: { value: 'https://new-url.com' } });

      expect(handleChange).toHaveBeenCalledTimes(1);
      expect(handleChange).toHaveBeenCalledWith('https://new-url.com');
    });

    it('calls onChange with updated value using userEvent', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      await user.click(input);
      await user.type(input, 'https://typed-url.com');

      // userEvent.type fires one onChange per character
      expect(handleChange).toHaveBeenCalled();
      expect(handleChange.mock.calls.length).toBeGreaterThan(0);
    });

    it('applies maxLength when provided', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: 100,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('maxLength', '100');
    });

    it('does not apply maxLength when null', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            maxLength: null,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).not.toHaveAttribute('maxLength');
    });

    it('sets autoComplete="url" on the input', () => {
      render(
        <UrlField {...buildProps({ mode: 'edit', value: '' })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('autoComplete', 'url');
    });

    it('sets placeholder when provided', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            placeholder: 'Enter URL…',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('placeholder', 'Enter URL…');
    });

    it('sets data-field-name attribute on the input', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: '', name: 'website' })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('data-field-name', 'website');
    });

    it('sets name attribute on the input', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: '', name: 'site_url' })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('name', 'site_url');
    });

    it('uses localValue when value is null (semi-uncontrolled mode)', async () => {
      const handleChange = vi.fn();
      const user = userEvent.setup();

      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: null,
            onChange: handleChange,
          })}
        />,
      );

      const input = screen.getByRole('textbox') as HTMLInputElement;
      // Initial localValue is ''
      expect(input.value).toBe('');

      await user.clear(input);
      await user.type(input, 'abc');

      // After typing, localValue should be updated via handleChange
      expect(handleChange).toHaveBeenCalled();
    });
  });

  // ========================================================================
  // URL Validation
  // ========================================================================
  describe('url validation', () => {
    it('uses native type="url" browser validation', () => {
      render(
        <UrlField {...buildProps({ mode: 'edit', value: '' })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('type', 'url');
    });

    it('sets required attribute when required=true', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: '', required: true })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('required');
    });

    it('sets aria-required when required=true', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: '', required: true })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('aria-required', 'true');
    });
  });

  // ========================================================================
  // New Window Behavior
  // ========================================================================
  describe('new window behavior', () => {
    it('defaults openTargetInNewWindow to true', () => {
      // Render without explicitly setting openTargetInNewWindow
      render(
        <UrlField
          {...buildProps({ mode: 'display', value: 'https://example.com' })}
        />,
      );

      const link = screen.getByRole('link');
      // Default is true → target="_blank"
      expect(link).toHaveAttribute('target', '_blank');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    });

    it('sets target="_blank" when openTargetInNewWindow=true', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: true,
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).toHaveAttribute('target', '_blank');
    });

    it('does not set target="_blank" when openTargetInNewWindow=false', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: false,
          })}
        />,
      );

      const link = screen.getByRole('link');
      expect(link).not.toHaveAttribute('target', '_blank');
      expect(link).toHaveAttribute('target', '_self');
    });

    it('shows external link icon only when openTargetInNewWindow is true', () => {
      const { container, rerender } = render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: true,
          })}
        />,
      );

      // Icon present when true
      expect(container.querySelector('svg')).toBeInTheDocument();

      // Re-render with false — icon should be gone
      rerender(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            openTargetInNewWindow: false,
          })}
        />,
      );

      expect(container.querySelector('svg')).not.toBeInTheDocument();
    });
  });

  // ========================================================================
  // Access Control
  // ========================================================================
  describe('access control', () => {
    it('renders normally with access="full"', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: 'https://example.com',
            access: 'full',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      expect(input).not.toBeDisabled();
      expect(input).toBeVisible();
    });

    it('renders as display mode with access="readonly"', () => {
      // readonly access forces effectiveMode = 'display'
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: 'https://example.com',
            access: 'readonly',
          })}
        />,
      );

      // Should render in display mode (link) not edit mode (input)
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      const link = screen.getByRole('link');
      expect(link).toBeInTheDocument();
      expect(link).toHaveAttribute('href', 'https://example.com');
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: 'https://example.com',
            access: 'forbidden',
          })}
        />,
      );

      // Forbidden renders a span with role="alert" and default accessDeniedMessage
      const alert = screen.getByRole('alert');
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveTextContent('access denied');

      // No input or link should be rendered
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders custom accessDeniedMessage with access="forbidden"', () => {
      render(
        <UrlField
          {...buildProps({
            access: 'forbidden',
            accessDeniedMessage: 'Insufficient permissions',
          })}
        />,
      );

      expect(screen.getByRole('alert')).toHaveTextContent(
        'Insufficient permissions',
      );
    });

    it('forbidden access renders data-field-name attribute', () => {
      render(
        <UrlField
          {...buildProps({
            name: 'restricted_url',
            access: 'forbidden',
          })}
        />,
      );

      const alert = screen.getByRole('alert');
      expect(alert).toHaveAttribute('data-field-name', 'restricted_url');
    });

    it('renders readonly empty value with emptyValueMessage', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: null,
            access: 'readonly',
          })}
        />,
      );

      // readonly forces display mode, null value → emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    });

    it('disabled input when disabled=true in edit mode', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: 'https://example.com',
            disabled: true,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeDisabled();
    });

    it('applies disabled styling class when disabled', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: 'https://example.com',
            disabled: true,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      // Component applies 'disabled:bg-gray-100' and 'disabled:text-gray-500'
      // These are Tailwind variants, but the base classes should be present
      expect(input).toHaveClass('disabled:bg-gray-100');
    });
  });

  // ========================================================================
  // Validation
  // ========================================================================
  describe('validation', () => {
    it('shows error state when error prop provided', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: 'invalid-url',
            error: 'Please enter a valid URL',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      // Component sets aria-invalid when error is truthy
      expect(input).toHaveAttribute('aria-invalid', 'true');
    });

    it('sets aria-describedby to error id when error is present', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            name: 'website',
            value: '',
            error: 'URL is required',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      // ariaDescribedBy = `${name}-error` = "website-error"
      expect(input).toHaveAttribute('aria-describedby', 'website-error');
    });

    it('applies error CSS classes when error is present', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            error: 'Invalid URL format',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      // Error state classes: border-red-500, focus:border-red-500, focus:ring-red-500
      expect(input).toHaveClass('border-red-500');
    });

    it('applies normal CSS classes when no error', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: 'https://ok.com' })}
        />,
      );

      const input = screen.getByRole('textbox');
      // Non-error state classes: border-gray-300
      expect(input).toHaveClass('border-gray-300');
    });

    it('sets aria-describedby to description when no error but description exists', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            name: 'homepage',
            value: '',
            description: 'Enter your website URL',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('aria-describedby', 'homepage-description');
    });

    it('does not set aria-invalid when no error', () => {
      render(
        <UrlField {...buildProps({ mode: 'edit', value: '' })} />,
      );

      const input = screen.getByRole('textbox');
      expect(input).not.toHaveAttribute('aria-invalid');
    });
  });

  // ========================================================================
  // Null / Empty Handling
  // ========================================================================
  describe('null/empty handling', () => {
    it('handles null value in display mode', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'display', value: null })}
        />,
      );

      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('handles null value in edit mode', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: null })}
        />,
      );

      const input = screen.getByRole('textbox') as HTMLInputElement;
      // When value is null, localValue '' is used
      expect(input.value).toBe('');
    });

    it('handles undefined value in display mode', () => {
      // TypeScript would flag this, but test runtime resilience
      render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: undefined as unknown as string | null,
          })}
        />,
      );

      // undefined is falsy, so emptyValueMessage should appear
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles empty string value in display mode', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'display', value: '' })}
        />,
      );

      // Empty string is falsy → emptyValueMessage
      expect(screen.getByText('no data')).toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('handles empty string value in edit mode', () => {
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: '' })}
        />,
      );

      const input = screen.getByRole('textbox') as HTMLInputElement;
      expect(input.value).toBe('');
    });
  });

  // ========================================================================
  // Visibility
  // ========================================================================
  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: 'https://example.com',
            isVisible: true,
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toBeInTheDocument();
      expect(input).toBeVisible();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: 'https://example.com',
            isVisible: false,
          })}
        />,
      );

      // Component returns null when isVisible=false
      expect(container.innerHTML).toBe('');
      expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
      expect(screen.queryByRole('link')).not.toBeInTheDocument();
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <UrlField
          {...buildProps({
            mode: 'display',
            value: 'https://example.com',
            isVisible: false,
          })}
        />,
      );

      expect(container.innerHTML).toBe('');
    });

    it('defaults isVisible to true', () => {
      // Render without explicit isVisible — defaults to true
      render(
        <UrlField
          {...buildProps({ mode: 'edit', value: 'https://example.com' })}
        />,
      );

      expect(screen.getByRole('textbox')).toBeInTheDocument();
    });
  });

  // ========================================================================
  // Label & ARIA
  // ========================================================================
  describe('label and aria integration', () => {
    it('sets aria-label when labelMode is hidden', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            label: 'Website URL',
            labelMode: 'hidden',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('aria-label', 'Website URL');
    });

    it('does not set aria-label when labelMode is not hidden', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            label: 'Website URL',
            labelMode: 'stacked',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).not.toHaveAttribute('aria-label');
    });

    it('uses fieldId as control id when provided', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            fieldId: 'custom-field-id',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('id', 'custom-field-id');
    });

    it('falls back to field-{name} as control id when fieldId is not provided', () => {
      render(
        <UrlField
          {...buildProps({
            mode: 'edit',
            value: '',
            name: 'homepage',
          })}
        />,
      );

      const input = screen.getByRole('textbox');
      expect(input).toHaveAttribute('id', 'field-homepage');
    });
  });
});
