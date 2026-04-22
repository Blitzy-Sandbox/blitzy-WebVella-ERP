/**
 * Vitest Component Tests for `<Button />`
 *
 * Validates the React Button component (`apps/frontend/src/components/common/Button.tsx`)
 * that replaces the monolith's `PcButton` ViewComponent
 * (`WebVella.Erp.Web/Components/PcButton/PcButton.cs`, `Display.cshtml`).
 *
 * The monolith's PcButtonOptions define 17 configuration properties:
 *  - is_visible (string → boolean): controls button visibility
 *  - type (WvButtonType): Button | Link | Submit element type
 *  - is_outline (bool): border-based styling instead of filled background
 *  - is_block (bool): full-width block button
 *  - is_active (bool): active/pressed visual state
 *  - is_disabled (bool): prevents interaction, applies muted styling
 *  - color (WvColor): White | Primary | Secondary | Success | Danger | Warning | Info | Light | Dark
 *  - size (WvCssSize): Inherit | Small | Normal | Large | ExtraLarge
 *  - class (string): additional CSS class names
 *  - id (string): HTML id attribute
 *  - text (string, default "button"): button label text
 *  - onclick (string): click handler (migrated to React onClick function)
 *  - href (string): navigation URL (renders <a> element)
 *  - new_tab (bool): opens href in a new browser tab
 *  - icon_class (string): CSS class for <i> icon element
 *  - icon_right (bool): positions icon to the right of text
 *  - form (string): associates submit button with a <form> by id
 *
 * Test coverage spans:
 *  - Rendering: default props, element type selection, text/children, visibility, id, className
 *  - Color variants: all 7 semantic colors + outline styling (Tailwind CSS classes)
 *  - Sizes: all 5 WvCssSize variants with Tailwind padding/text classes
 *  - Disabled state: attribute presence, visual indicator, click prevention, anchor disabled
 *  - Click handlers: invocation, event argument, graceful absence
 *  - Icons: left/right positioning, absence, icon+text coexistence
 *  - Block: full-width rendering via w-full class
 *  - Active: ring/focus visual classes
 *  - Link/Anchor: href, newTab target/rel attributes, default no-target
 *  - Form integration: form attribute passthrough
 *  - Accessibility: focus ring classes, anchor role, disabled ARIA
 *  - Loading: interaction prevention via isDisabled pattern
 *
 * @see apps/frontend/src/components/common/Button.tsx
 * @see WebVella.Erp.Web/Components/PcButton/PcButton.cs
 * @see WebVella.Erp.Web/Components/PcButton/Display.cshtml
 * @see WebVella.Erp.Web/Components/PcButton/Options.cshtml
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import Button, {
  ButtonType,
  ButtonColor,
  ButtonSize,
} from '../../../src/components/common/Button';
import type { ButtonProps } from '../../../src/components/common/Button';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Factory for creating ButtonProps with sensible defaults and overrides.
 * Mirrors the PcButtonOptions defaults from PcButton.cs (lines 25-78):
 *  - Text: "button", Type: Button, Color: White, Size: Inherit
 *  - isVisible: true, isOutline/isBlock/isActive/isDisabled: false
 */
function createProps(overrides: Partial<ButtonProps> = {}): ButtonProps {
  return { ...overrides };
}

// ---------------------------------------------------------------------------
// Test Suites
// ---------------------------------------------------------------------------

describe('Button Component', () => {
  // =========================================================================
  // 2.1 — Rendering Tests
  // =========================================================================
  describe('Rendering', () => {
    it('renders with default props', () => {
      render(<Button />);
      // Default text from PcButtonOptions.Text is "button"
      expect(screen.getByText('button')).toBeInTheDocument();
    });

    it('renders as a button element by default', () => {
      render(<Button />);
      const btn = screen.getByRole('button');
      expect(btn.tagName).toBe('BUTTON');
      expect(btn).toHaveAttribute('type', 'button');
    });

    it('renders as an anchor element when href is provided', () => {
      render(<Button href="/some-path" text="Navigate" />);
      const link = screen.getByRole('link', { name: 'Navigate' });
      expect(link.tagName).toBe('A');
      expect(link).toHaveAttribute('href', '/some-path');
    });

    it('renders as an anchor element when type is Link', () => {
      render(<Button type={ButtonType.Link} text="Link Button" />);
      const link = screen.getByRole('link', { name: 'Link Button' });
      expect(link.tagName).toBe('A');
      // Default href is '#' when no href is provided for Link type
      expect(link).toHaveAttribute('href', '#');
    });

    it('renders as submit button when type is Submit', () => {
      render(<Button type={ButtonType.Submit} text="Submit Form" />);
      const btn = screen.getByRole('button', { name: 'Submit Form' });
      expect(btn.tagName).toBe('BUTTON');
      expect(btn).toHaveAttribute('type', 'submit');
    });

    it('renders custom text', () => {
      render(<Button text="Custom Label" />);
      expect(screen.getByText('Custom Label')).toBeInTheDocument();
    });

    it('renders children when provided', () => {
      render(
        <Button>
          <em>Child Content</em>
        </Button>,
      );
      expect(screen.getByText('Child Content')).toBeInTheDocument();
      // Children take precedence — default "button" text should not appear
      expect(screen.queryByText('button')).toBeNull();
    });

    it('renders nothing when isVisible is false', () => {
      const { container } = render(<Button isVisible={false} text="Hidden" />);
      expect(container.firstChild).toBeNull();
      expect(screen.queryByText('Hidden')).toBeNull();
    });

    it('renders the id attribute', () => {
      render(<Button id="my-btn" text="ID Test" />);
      const btn = screen.getByRole('button', { name: 'ID Test' });
      expect(btn).toHaveAttribute('id', 'my-btn');
    });

    it('applies custom className', () => {
      render(<Button className="extra-class" text="Class Test" />);
      const btn = screen.getByRole('button', { name: 'Class Test' });
      expect(btn).toHaveClass('extra-class');
    });
  });

  // =========================================================================
  // 2.2 — Button Variant Tests (Color — from PcButtonOptions.Color / WvColor)
  // =========================================================================
  describe('Color Variants', () => {
    it('renders primary color variant', () => {
      render(<Button color={ButtonColor.Primary} text="Primary" />);
      const btn = screen.getByRole('button', { name: 'Primary' });
      expect(btn).toHaveClass('bg-blue-600');
      expect(btn).toHaveClass('text-white');
    });

    it('renders secondary color variant', () => {
      render(<Button color={ButtonColor.Secondary} text="Secondary" />);
      const btn = screen.getByRole('button', { name: 'Secondary' });
      expect(btn).toHaveClass('bg-gray-600');
      expect(btn).toHaveClass('text-white');
    });

    it('renders success color variant', () => {
      render(<Button color={ButtonColor.Success} text="Success" />);
      const btn = screen.getByRole('button', { name: 'Success' });
      expect(btn).toHaveClass('bg-green-600');
      expect(btn).toHaveClass('text-white');
    });

    it('renders danger color variant', () => {
      render(<Button color={ButtonColor.Danger} text="Danger" />);
      const btn = screen.getByRole('button', { name: 'Danger' });
      expect(btn).toHaveClass('bg-red-600');
      expect(btn).toHaveClass('text-white');
    });

    it('renders warning color variant', () => {
      render(<Button color={ButtonColor.Warning} text="Warning" />);
      const btn = screen.getByRole('button', { name: 'Warning' });
      expect(btn).toHaveClass('bg-yellow-500');
      expect(btn).toHaveClass('text-gray-900');
    });

    it('renders info color variant', () => {
      render(<Button color={ButtonColor.Info} text="Info" />);
      const btn = screen.getByRole('button', { name: 'Info' });
      expect(btn).toHaveClass('bg-cyan-500');
      expect(btn).toHaveClass('text-white');
    });

    it('renders default white color', () => {
      // Default color is White (from PcButtonOptions default WvColor.White)
      render(<Button text="Default" />);
      const btn = screen.getByRole('button', { name: 'Default' });
      expect(btn).toHaveClass('bg-white');
      expect(btn).toHaveClass('text-gray-800');
      expect(btn).toHaveClass('border-gray-300');
    });

    it('renders outline variant with border-based styling', () => {
      render(
        <Button
          isOutline={true}
          color={ButtonColor.Primary}
          text="Outline"
        />,
      );
      const btn = screen.getByRole('button', { name: 'Outline' });
      // Outline uses border + transparent background, not filled background
      expect(btn).toHaveClass('border-blue-600');
      expect(btn).toHaveClass('text-blue-600');
      expect(btn).toHaveClass('bg-transparent');
      // Must NOT have the solid fill class
      expect(btn).not.toHaveClass('bg-blue-600');
    });
  });

  // =========================================================================
  // 2.3 — Size Tests (from PcButtonOptions.Size / WvCssSize)
  // =========================================================================
  describe('Sizes', () => {
    it('renders default (inherit) size', () => {
      // Default size is Inherit — only applies base rounded-md
      render(<Button text="Inherit" />);
      const btn = screen.getByRole('button', { name: 'Inherit' });
      expect(btn).toHaveClass('rounded-md');
      // Should NOT have explicit padding or text-size classes from other sizes
      expect(btn).not.toHaveClass('px-3');
      expect(btn).not.toHaveClass('text-sm');
    });

    it('renders small size', () => {
      render(<Button size={ButtonSize.Small} text="Small" />);
      const btn = screen.getByRole('button', { name: 'Small' });
      expect(btn).toHaveClass('px-3');
      expect(btn).toHaveClass('py-1.5');
      expect(btn).toHaveClass('text-sm');
      expect(btn).toHaveClass('rounded');
    });

    it('renders normal size', () => {
      render(<Button size={ButtonSize.Normal} text="Normal" />);
      const btn = screen.getByRole('button', { name: 'Normal' });
      expect(btn).toHaveClass('px-4');
      expect(btn).toHaveClass('py-2');
      expect(btn).toHaveClass('text-base');
      expect(btn).toHaveClass('rounded-md');
    });

    it('renders large size', () => {
      render(<Button size={ButtonSize.Large} text="Large" />);
      const btn = screen.getByRole('button', { name: 'Large' });
      expect(btn).toHaveClass('px-6');
      expect(btn).toHaveClass('py-3');
      expect(btn).toHaveClass('text-lg');
      expect(btn).toHaveClass('rounded-lg');
    });

    it('renders extra large size', () => {
      render(<Button size={ButtonSize.ExtraLarge} text="XL" />);
      const btn = screen.getByRole('button', { name: 'XL' });
      expect(btn).toHaveClass('px-8');
      expect(btn).toHaveClass('py-4');
      expect(btn).toHaveClass('text-xl');
      expect(btn).toHaveClass('rounded-xl');
    });
  });

  // =========================================================================
  // 2.4 — Disabled State Tests (from PcButtonOptions.isDisabled)
  // =========================================================================
  describe('Disabled State', () => {
    it('renders disabled state on button element', () => {
      render(<Button isDisabled={true} text="Disabled" />);
      const btn = screen.getByRole('button', { name: 'Disabled' });
      expect(btn).toBeDisabled();
    });

    it('disabled button has visual indicator classes', () => {
      render(<Button isDisabled={true} text="Muted" />);
      const btn = screen.getByRole('button', { name: 'Muted' });
      expect(btn).toHaveClass('opacity-50');
      expect(btn).toHaveClass('cursor-not-allowed');
      expect(btn).toHaveClass('pointer-events-none');
    });

    it('disabled button does not trigger onClick', async () => {
      const handler = vi.fn();
      render(<Button isDisabled={true} onClick={handler} text="NoClick" />);
      const btn = screen.getByRole('button', { name: 'NoClick' });
      // userEvent respects the HTML disabled attribute and won't fire
      const user = userEvent.setup();
      await user.click(btn);
      expect(handler).not.toHaveBeenCalled();
    });

    it('disabled anchor has aria-disabled and pointer-events-none', () => {
      render(
        <Button
          type={ButtonType.Link}
          isDisabled={true}
          href="/blocked"
          text="DisabledLink"
        />,
      );
      const link = screen.getByRole('link', { name: 'DisabledLink' });
      expect(link).toHaveAttribute('aria-disabled', 'true');
      expect(link).toHaveClass('pointer-events-none');
      expect(link).toHaveClass('opacity-50');
      // Disabled anchor should also have tabIndex -1 to prevent keyboard focus
      expect(link).toHaveAttribute('tabindex', '-1');
    });
  });

  // =========================================================================
  // 2.5 — Click Handler Tests (from PcButtonOptions.OnClick)
  // =========================================================================
  describe('Click Handlers', () => {
    it('calls onClick handler when clicked', async () => {
      const handler = vi.fn();
      render(<Button onClick={handler} text="Click Me" />);
      const user = userEvent.setup();
      await user.click(screen.getByRole('button', { name: 'Click Me' }));
      expect(handler).toHaveBeenCalledTimes(1);
    });

    it('click handler receives event object', async () => {
      const handler = vi.fn();
      render(<Button onClick={handler} text="Event" />);
      const user = userEvent.setup();
      await user.click(screen.getByRole('button', { name: 'Event' }));
      expect(handler).toHaveBeenCalledTimes(1);
      // React SyntheticEvent wraps the native click event
      const syntheticEvent = handler.mock.calls[0][0];
      expect(syntheticEvent).toBeDefined();
      expect(syntheticEvent.type).toBe('click');
    });

    it('does not crash without onClick', () => {
      render(<Button text="No Handler" />);
      const btn = screen.getByRole('button', { name: 'No Handler' });
      // Clicking without a handler should not throw
      expect(() => fireEvent.click(btn)).not.toThrow();
    });
  });

  // =========================================================================
  // 2.6 — Icon Support Tests (from PcButtonOptions.IconClass & IconRight)
  // =========================================================================
  describe('Icons', () => {
    it('renders left icon by default', () => {
      const { container } = render(
        <Button iconClass="fas fa-check" text="OK" />,
      );
      const icon = container.querySelector('i.fas.fa-check');
      expect(icon).toBeInTheDocument();
      expect(icon).toHaveAttribute('aria-hidden', 'true');
      // Icon should appear before the text span in DOM order
      const btn = screen.getByRole('button', { name: 'OK' });
      const children = Array.from(btn.childNodes);
      const iconIndex = children.indexOf(icon as ChildNode);
      const textSpan = container.querySelector('span');
      const textIndex = children.indexOf(textSpan as ChildNode);
      expect(iconIndex).toBeLessThan(textIndex);
    });

    it('renders right icon when iconRight is true', () => {
      const { container } = render(
        <Button iconClass="fas fa-arrow-right" iconRight={true} text="Next" />,
      );
      const icon = container.querySelector('i.fas.fa-arrow-right');
      expect(icon).toBeInTheDocument();
      // Icon should appear after the text span in DOM order
      const btn = screen.getByRole('button', { name: 'Next' });
      const children = Array.from(btn.childNodes);
      const iconIndex = children.indexOf(icon as ChildNode);
      const textSpan = container.querySelector('span');
      const textIndex = children.indexOf(textSpan as ChildNode);
      expect(iconIndex).toBeGreaterThan(textIndex);
    });

    it('renders without icon when iconClass is empty', () => {
      const { container } = render(<Button text="No Icon" />);
      const icon = container.querySelector('i');
      expect(icon).toBeNull();
    });

    it('renders icon and text together', () => {
      const { container } = render(
        <Button iconClass="fas fa-save" text="Save" />,
      );
      // Both icon and text should be present
      const icon = container.querySelector('i.fas.fa-save');
      expect(icon).toBeInTheDocument();
      expect(screen.getByText('Save')).toBeInTheDocument();
    });
  });

  // =========================================================================
  // 2.7 — Block Button Test (from PcButtonOptions.isBlock)
  // =========================================================================
  describe('Block Button', () => {
    it('renders full width when isBlock is true', () => {
      render(<Button isBlock={true} text="Block" />);
      const btn = screen.getByRole('button', { name: 'Block' });
      expect(btn).toHaveClass('w-full');
    });

    it('does not apply w-full when isBlock is false', () => {
      render(<Button isBlock={false} text="Inline" />);
      const btn = screen.getByRole('button', { name: 'Inline' });
      expect(btn).not.toHaveClass('w-full');
    });
  });

  // =========================================================================
  // 2.8 — Active State Test (from PcButtonOptions.isActive)
  // =========================================================================
  describe('Active State', () => {
    it('renders active state with ring classes', () => {
      render(<Button isActive={true} text="Active" />);
      const btn = screen.getByRole('button', { name: 'Active' });
      expect(btn).toHaveClass('ring-2');
      expect(btn).toHaveClass('ring-offset-1');
    });

    it('does not apply active ring when isActive is false', () => {
      render(<Button isActive={false} text="Inactive" />);
      const btn = screen.getByRole('button', { name: 'Inactive' });
      // ring-2 should not be present when not active (not from activeClass)
      const classList = btn.className;
      // The BASE_CLASSES include focus-visible:ring-2, but not ring-2 alone
      expect(classList).not.toContain(' ring-2 ');
    });
  });

  // =========================================================================
  // 2.9 — Link / Anchor Tests (from PcButtonOptions.Href & NewTab)
  // =========================================================================
  describe('Link / Anchor', () => {
    it('renders href on anchor element', () => {
      render(<Button href="/dashboard" text="Go" />);
      const link = screen.getByRole('link', { name: 'Go' });
      expect(link).toHaveAttribute('href', '/dashboard');
    });

    it('opens in new tab when newTab is true', () => {
      render(
        <Button href="/external" newTab={true} text="External" />,
      );
      const link = screen.getByRole('link', { name: 'External' });
      expect(link).toHaveAttribute('target', '_blank');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    });

    it('does not add target attribute when newTab is false', () => {
      render(<Button href="/internal" newTab={false} text="Internal" />);
      const link = screen.getByRole('link', { name: 'Internal' });
      expect(link).not.toHaveAttribute('target');
      expect(link).not.toHaveAttribute('rel');
    });
  });

  // =========================================================================
  // 2.10 — Form Integration Test (from PcButtonOptions.Form)
  // =========================================================================
  describe('Form Integration', () => {
    it('passes form attribute for submit buttons', () => {
      render(
        <Button
          type={ButtonType.Submit}
          form="my-form"
          text="Submit"
        />,
      );
      const btn = screen.getByRole('button', { name: 'Submit' });
      expect(btn).toHaveAttribute('form', 'my-form');
      expect(btn).toHaveAttribute('type', 'submit');
    });

    it('does not render form attribute when not provided', () => {
      render(<Button type={ButtonType.Submit} text="Plain Submit" />);
      const btn = screen.getByRole('button', { name: 'Plain Submit' });
      // form attribute should not be present when not provided
      expect(btn).not.toHaveAttribute('form');
    });
  });

  // =========================================================================
  // 2.11 — Accessibility Tests
  // =========================================================================
  describe('Accessibility', () => {
    it('button has proper focus ring classes', () => {
      render(<Button text="Focus" />);
      const btn = screen.getByRole('button', { name: 'Focus' });
      // BASE_CLASSES include focus:outline-none and focus-visible:ring-2
      expect(btn.className).toContain('focus:outline-none');
      expect(btn.className).toContain('focus-visible:ring-2');
      expect(btn.className).toContain('focus-visible:ring-offset-2');
    });

    it('anchor link has accessible role', () => {
      render(<Button href="/about" text="About" />);
      // <a> with href natively has role="link" — no explicit role needed
      const link = screen.getByRole('link', { name: 'About' });
      expect(link).toBeInTheDocument();
      // Should not have conflicting role attribute
      expect(link).not.toHaveAttribute('role');
    });

    it('disabled button is communicated to assistive technology', () => {
      render(<Button isDisabled={true} text="Disabled AT" />);
      const btn = screen.getByRole('button', { name: 'Disabled AT' });
      // Native disabled attribute communicates state to screen readers
      expect(btn).toBeDisabled();
      expect(btn).toHaveAttribute('disabled');
    });

    it('disabled anchor communicates state via aria-disabled', () => {
      render(
        <Button
          type={ButtonType.Link}
          isDisabled={true}
          text="Disabled Link AT"
        />,
      );
      const link = screen.getByRole('link', { name: 'Disabled Link AT' });
      // Anchors use aria-disabled since HTML disabled is not valid on <a>
      expect(link).toHaveAttribute('aria-disabled', 'true');
    });
  });

  // =========================================================================
  // 2.12 — Loading State Tests
  //
  // The Button component delegates loading state to consumer components via
  // the `isDisabled` prop. When a parent component is performing an async
  // operation, it passes `isDisabled={true}` to prevent user interaction.
  // This section validates that pattern works correctly.
  // =========================================================================
  describe('Loading State (via isDisabled)', () => {
    it('renders loading-equivalent state when isDisabled is true', () => {
      // Consumer pattern: pass isDisabled during async operations
      render(<Button isDisabled={true} text="Saving..." />);
      const btn = screen.getByRole('button', { name: 'Saving...' });
      expect(btn).toBeDisabled();
      expect(btn).toHaveClass('opacity-50');
      expect(btn).toHaveClass('cursor-not-allowed');
    });

    it('loading state (isDisabled) prevents interaction', async () => {
      const handler = vi.fn();
      render(
        <Button isDisabled={true} onClick={handler} text="Processing..." />,
      );
      const btn = screen.getByRole('button', { name: 'Processing...' });
      const user = userEvent.setup();
      await user.click(btn);
      // Handler must NOT be called while in loading/disabled state
      expect(handler).not.toHaveBeenCalled();
    });
  });

  // =========================================================================
  // Additional Edge Case Tests
  // =========================================================================
  describe('Edge Cases', () => {
    it('stores legacy string onClick as data-onclick attribute', () => {
      render(<Button onClick="alert('hello')" text="Legacy" />);
      const btn = screen.getByRole('button', { name: 'Legacy' });
      expect(btn).toHaveAttribute('data-onclick', "alert('hello')");
    });

    it('renders anchor with default href # when type is Link and no href', () => {
      render(<Button type={ButtonType.Link} text="Hash Link" />);
      const link = screen.getByRole('link', { name: 'Hash Link' });
      expect(link).toHaveAttribute('href', '#');
    });

    it('applies base classes to all button variants', () => {
      render(<Button text="Base" />);
      const btn = screen.getByRole('button', { name: 'Base' });
      expect(btn).toHaveClass('inline-flex');
      expect(btn).toHaveClass('items-center');
      expect(btn).toHaveClass('justify-center');
      expect(btn).toHaveClass('font-medium');
    });

    it('applies base classes to anchor variants', () => {
      render(<Button href="/test" text="Base Link" />);
      const link = screen.getByRole('link', { name: 'Base Link' });
      expect(link).toHaveClass('inline-flex');
      expect(link).toHaveClass('items-center');
      expect(link).toHaveClass('justify-center');
      expect(link).toHaveClass('font-medium');
    });

    it('combines multiple props correctly', () => {
      const handler = vi.fn();
      const { container } = render(
        <Button
          color={ButtonColor.Success}
          size={ButtonSize.Large}
          isOutline={true}
          isBlock={true}
          iconClass="fas fa-check"
          id="combo-btn"
          className="my-custom"
          text="Combo"
          onClick={handler}
        />,
      );
      const btn = screen.getByRole('button', { name: 'Combo' });
      // Outline success classes
      expect(btn).toHaveClass('border-green-600');
      expect(btn).toHaveClass('bg-transparent');
      // Large size classes
      expect(btn).toHaveClass('px-6');
      expect(btn).toHaveClass('text-lg');
      // Block class
      expect(btn).toHaveClass('w-full');
      // Custom class
      expect(btn).toHaveClass('my-custom');
      // ID
      expect(btn).toHaveAttribute('id', 'combo-btn');
      // Icon present
      const icon = container.querySelector('i.fas.fa-check');
      expect(icon).toBeInTheDocument();
    });
  });
});
