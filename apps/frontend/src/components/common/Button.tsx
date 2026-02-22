import type { MouseEventHandler, ReactNode } from 'react';

// ---------------------------------------------------------------------------
// Enums — ButtonType, ButtonColor, ButtonSize
// ---------------------------------------------------------------------------

/**
 * Button element type — determines the rendered HTML element.
 * Maps from C# WvButtonType enum (WebVella.TagHelpers):
 *   Button = 0, Link = 1, Submit = 2
 */
export enum ButtonType {
  /** Standard `<button type="button">` element (default). */
  Button = 'button',
  /** Renders an `<a>` element styled as a button. */
  Link = 'link',
  /** Renders `<button type="submit">` for form submission. */
  Submit = 'submit',
}

/**
 * Button color variant — maps from C# WvColor enum (WebVella.TagHelpers).
 * Includes Bootstrap semantic colors and the full Material Design palette.
 *
 * C# enum values:
 *   White = 0, Primary = 1, Secondary = 2, Success = 3, Danger = 4,
 *   Warning = 5, Info = 6, Light = 7, Dark = 8, Red = 10, Pink = 11,
 *   Purple = 12, DeepPurple = 13, Indigo = 14, Blue = 15, LightBlue = 16,
 *   Cyan = 17, Teal = 18, Green = 19, LightGreen = 20, Lime = 21,
 *   Yellow = 22, Amber = 23, Orange = 24, DeepOrange = 25
 */
export enum ButtonColor {
  White = 'white',
  Primary = 'primary',
  Secondary = 'secondary',
  Success = 'success',
  Danger = 'danger',
  Warning = 'warning',
  Info = 'info',
  Light = 'light',
  Dark = 'dark',
  Red = 'red',
  Pink = 'pink',
  Purple = 'purple',
  DeepPurple = 'deep-purple',
  Indigo = 'indigo',
  Blue = 'blue',
  LightBlue = 'light-blue',
  Cyan = 'cyan',
  Teal = 'teal',
  Green = 'green',
  LightGreen = 'light-green',
  Lime = 'lime',
  Yellow = 'yellow',
  Amber = 'amber',
  Orange = 'orange',
  DeepOrange = 'deep-orange',
}

/**
 * Button size variant — maps from C# WvCssSize enum (WebVella.TagHelpers):
 *   Inherit = 0, Small = 1, Normal = 2, Large = 3, ExtraLarge = 4
 */
export enum ButtonSize {
  /** Inherits sizing from parent context — applies default rounding only. */
  Inherit = 'inherit',
  /** Small button — compact padding, smaller text. */
  Small = 'sm',
  /** Normal / default button size. */
  Normal = 'md',
  /** Large button — increased padding and text. */
  Large = 'lg',
  /** Extra-large button — maximum padding and text. */
  ExtraLarge = 'xl',
}

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the `Button` component.
 *
 * Maps every property from the monolith's `PcButtonOptions` class
 * (`PcButton.cs` lines 25-78) plus React-specific `children` prop.
 */
export interface ButtonProps {
  /**
   * Conditional rendering — when `false`, component returns `null`.
   * Source: `PcButtonOptions.IsVisible` (data-source evaluated to boolean).
   * @default true
   */
  isVisible?: boolean;

  /**
   * Button element type — determines the rendered HTML element.
   * Source: `PcButtonOptions.Type` (WvButtonType enum).
   * @default ButtonType.Button
   */
  type?: ButtonType;

  /**
   * Whether to use the outline style variant (border + text color, no fill).
   * Source: `PcButtonOptions.isOutline`.
   * @default false
   */
  isOutline?: boolean;

  /**
   * Full-width block button — spans the entire container width.
   * Source: `PcButtonOptions.isBlock`.
   * @default false
   */
  isBlock?: boolean;

  /**
   * Active / pressed visual state.
   * Source: `PcButtonOptions.isActive`.
   * @default false
   */
  isActive?: boolean;

  /**
   * Disabled state — prevents interaction and applies muted styling.
   * Source: `PcButtonOptions.isDisabled`.
   * @default false
   */
  isDisabled?: boolean;

  /**
   * Color variant for background (solid) or border/text (outline).
   * Source: `PcButtonOptions.Color` (WvColor enum).
   * @default ButtonColor.White
   */
  color?: ButtonColor;

  /**
   * Size variant controlling padding, font-size, and border-radius.
   * Source: `PcButtonOptions.Size` (WvCssSize enum).
   * @default ButtonSize.Inherit
   */
  size?: ButtonSize;

  /**
   * Additional CSS class names appended to the element.
   * Source: `PcButtonOptions.Class`.
   */
  className?: string;

  /**
   * HTML `id` attribute applied to the rendered element.
   * Source: `PcButtonOptions.Id`.
   */
  id?: string;

  /**
   * Button label text. Ignored when `children` is provided.
   * Source: `PcButtonOptions.Text`.
   * @default "button"
   */
  text?: string;

  /**
   * Click handler — accepts a standard React mouse-event handler or a legacy
   * onclick string from the monolith. When a string is supplied the value is
   * stored as a `data-onclick` attribute instead of bound as an event handler.
   * Source: `PcButtonOptions.OnClick`.
   */
  onClick?: MouseEventHandler<HTMLButtonElement | HTMLAnchorElement> | string;

  /**
   * Navigation URL. When provided (or when `type` is `Link`), an `<a>`
   * element is rendered instead of `<button>`.
   * Source: `PcButtonOptions.Href` (data-source processed).
   */
  href?: string;

  /**
   * Open `href` in a new browser tab (`target="_blank"`).
   * Source: `PcButtonOptions.NewTab`.
   * @default false
   */
  newTab?: boolean;

  /**
   * CSS class for the icon element (e.g. a FontAwesome class such as
   * `"fas fa-check"`). Renders an `<i>` element next to the text.
   * Source: `PcButtonOptions.IconClass`.
   */
  iconClass?: string;

  /**
   * Position the icon to the right of the text instead of the default left.
   * Source: `PcButtonOptions.IconRight`.
   * @default false
   */
  iconRight?: boolean;

  /**
   * HTML `form` attribute — associates a submit button with a `<form>` by id.
   * Source: `PcButtonOptions.Form`.
   */
  form?: string;

  /**
   * React children — when provided, takes precedence over the `text` prop
   * for rendering button content.
   */
  children?: ReactNode;
}

// ---------------------------------------------------------------------------
// Tailwind CSS Class Maps (replacing Bootstrap 4 .btn-* classes)
// ---------------------------------------------------------------------------

/**
 * Solid (filled) color classes for each `ButtonColor` variant.
 * Each entry includes background, text color, hover state, and focus ring.
 */
const SOLID_COLOR_CLASSES: Record<ButtonColor, string> = {
  [ButtonColor.White]:
    'bg-white text-gray-800 border border-gray-300 hover:bg-gray-50 focus-visible:ring-gray-400',
  [ButtonColor.Primary]:
    'bg-blue-600 text-white hover:bg-blue-700 focus-visible:ring-blue-500',
  [ButtonColor.Secondary]:
    'bg-gray-600 text-white hover:bg-gray-700 focus-visible:ring-gray-500',
  [ButtonColor.Success]:
    'bg-green-600 text-white hover:bg-green-700 focus-visible:ring-green-500',
  [ButtonColor.Danger]:
    'bg-red-600 text-white hover:bg-red-700 focus-visible:ring-red-500',
  [ButtonColor.Warning]:
    'bg-yellow-500 text-gray-900 hover:bg-yellow-600 focus-visible:ring-yellow-400',
  [ButtonColor.Info]:
    'bg-cyan-500 text-white hover:bg-cyan-600 focus-visible:ring-cyan-400',
  [ButtonColor.Light]:
    'bg-gray-100 text-gray-800 hover:bg-gray-200 focus-visible:ring-gray-300',
  [ButtonColor.Dark]:
    'bg-gray-800 text-white hover:bg-gray-900 focus-visible:ring-gray-600',
  [ButtonColor.Red]:
    'bg-red-500 text-white hover:bg-red-600 focus-visible:ring-red-400',
  [ButtonColor.Pink]:
    'bg-pink-500 text-white hover:bg-pink-600 focus-visible:ring-pink-400',
  [ButtonColor.Purple]:
    'bg-purple-600 text-white hover:bg-purple-700 focus-visible:ring-purple-500',
  [ButtonColor.DeepPurple]:
    'bg-purple-800 text-white hover:bg-purple-900 focus-visible:ring-purple-600',
  [ButtonColor.Indigo]:
    'bg-indigo-600 text-white hover:bg-indigo-700 focus-visible:ring-indigo-500',
  [ButtonColor.Blue]:
    'bg-blue-500 text-white hover:bg-blue-600 focus-visible:ring-blue-400',
  [ButtonColor.LightBlue]:
    'bg-sky-400 text-white hover:bg-sky-500 focus-visible:ring-sky-300',
  [ButtonColor.Cyan]:
    'bg-cyan-500 text-white hover:bg-cyan-600 focus-visible:ring-cyan-400',
  [ButtonColor.Teal]:
    'bg-teal-500 text-white hover:bg-teal-600 focus-visible:ring-teal-400',
  [ButtonColor.Green]:
    'bg-green-500 text-white hover:bg-green-600 focus-visible:ring-green-400',
  [ButtonColor.LightGreen]:
    'bg-lime-500 text-gray-900 hover:bg-lime-600 focus-visible:ring-lime-400',
  [ButtonColor.Lime]:
    'bg-lime-400 text-gray-900 hover:bg-lime-500 focus-visible:ring-lime-300',
  [ButtonColor.Yellow]:
    'bg-yellow-400 text-gray-900 hover:bg-yellow-500 focus-visible:ring-yellow-300',
  [ButtonColor.Amber]:
    'bg-amber-500 text-gray-900 hover:bg-amber-600 focus-visible:ring-amber-400',
  [ButtonColor.Orange]:
    'bg-orange-500 text-white hover:bg-orange-600 focus-visible:ring-orange-400',
  [ButtonColor.DeepOrange]:
    'bg-orange-700 text-white hover:bg-orange-800 focus-visible:ring-orange-600',
};

/**
 * Outline color classes for each `ButtonColor` variant.
 * Each entry includes border, text color, hover-fill, and focus ring.
 */
const OUTLINE_COLOR_CLASSES: Record<ButtonColor, string> = {
  [ButtonColor.White]:
    'border border-gray-300 text-gray-800 bg-transparent hover:bg-white hover:text-gray-900 focus-visible:ring-gray-400',
  [ButtonColor.Primary]:
    'border border-blue-600 text-blue-600 bg-transparent hover:bg-blue-600 hover:text-white focus-visible:ring-blue-500',
  [ButtonColor.Secondary]:
    'border border-gray-600 text-gray-600 bg-transparent hover:bg-gray-600 hover:text-white focus-visible:ring-gray-500',
  [ButtonColor.Success]:
    'border border-green-600 text-green-600 bg-transparent hover:bg-green-600 hover:text-white focus-visible:ring-green-500',
  [ButtonColor.Danger]:
    'border border-red-600 text-red-600 bg-transparent hover:bg-red-600 hover:text-white focus-visible:ring-red-500',
  [ButtonColor.Warning]:
    'border border-yellow-500 text-yellow-600 bg-transparent hover:bg-yellow-500 hover:text-gray-900 focus-visible:ring-yellow-400',
  [ButtonColor.Info]:
    'border border-cyan-500 text-cyan-600 bg-transparent hover:bg-cyan-500 hover:text-white focus-visible:ring-cyan-400',
  [ButtonColor.Light]:
    'border border-gray-200 text-gray-600 bg-transparent hover:bg-gray-100 hover:text-gray-800 focus-visible:ring-gray-300',
  [ButtonColor.Dark]:
    'border border-gray-800 text-gray-800 bg-transparent hover:bg-gray-800 hover:text-white focus-visible:ring-gray-600',
  [ButtonColor.Red]:
    'border border-red-500 text-red-500 bg-transparent hover:bg-red-500 hover:text-white focus-visible:ring-red-400',
  [ButtonColor.Pink]:
    'border border-pink-500 text-pink-500 bg-transparent hover:bg-pink-500 hover:text-white focus-visible:ring-pink-400',
  [ButtonColor.Purple]:
    'border border-purple-600 text-purple-600 bg-transparent hover:bg-purple-600 hover:text-white focus-visible:ring-purple-500',
  [ButtonColor.DeepPurple]:
    'border border-purple-800 text-purple-800 bg-transparent hover:bg-purple-800 hover:text-white focus-visible:ring-purple-600',
  [ButtonColor.Indigo]:
    'border border-indigo-600 text-indigo-600 bg-transparent hover:bg-indigo-600 hover:text-white focus-visible:ring-indigo-500',
  [ButtonColor.Blue]:
    'border border-blue-500 text-blue-500 bg-transparent hover:bg-blue-500 hover:text-white focus-visible:ring-blue-400',
  [ButtonColor.LightBlue]:
    'border border-sky-400 text-sky-500 bg-transparent hover:bg-sky-400 hover:text-white focus-visible:ring-sky-300',
  [ButtonColor.Cyan]:
    'border border-cyan-500 text-cyan-600 bg-transparent hover:bg-cyan-500 hover:text-white focus-visible:ring-cyan-400',
  [ButtonColor.Teal]:
    'border border-teal-500 text-teal-600 bg-transparent hover:bg-teal-500 hover:text-white focus-visible:ring-teal-400',
  [ButtonColor.Green]:
    'border border-green-500 text-green-600 bg-transparent hover:bg-green-500 hover:text-white focus-visible:ring-green-400',
  [ButtonColor.LightGreen]:
    'border border-lime-500 text-lime-600 bg-transparent hover:bg-lime-500 hover:text-gray-900 focus-visible:ring-lime-400',
  [ButtonColor.Lime]:
    'border border-lime-400 text-lime-500 bg-transparent hover:bg-lime-400 hover:text-gray-900 focus-visible:ring-lime-300',
  [ButtonColor.Yellow]:
    'border border-yellow-400 text-yellow-500 bg-transparent hover:bg-yellow-400 hover:text-gray-900 focus-visible:ring-yellow-300',
  [ButtonColor.Amber]:
    'border border-amber-500 text-amber-600 bg-transparent hover:bg-amber-500 hover:text-gray-900 focus-visible:ring-amber-400',
  [ButtonColor.Orange]:
    'border border-orange-500 text-orange-500 bg-transparent hover:bg-orange-500 hover:text-white focus-visible:ring-orange-400',
  [ButtonColor.DeepOrange]:
    'border border-orange-700 text-orange-700 bg-transparent hover:bg-orange-700 hover:text-white focus-visible:ring-orange-600',
};

/**
 * Size classes for each `ButtonSize` variant.
 * Each entry includes padding, font-size, and border-radius.
 */
const SIZE_CLASSES: Record<ButtonSize, string> = {
  [ButtonSize.Inherit]: 'rounded-md',
  [ButtonSize.Small]: 'px-3 py-1.5 text-sm rounded',
  [ButtonSize.Normal]: 'px-4 py-2 text-base rounded-md',
  [ButtonSize.Large]: 'px-6 py-3 text-lg rounded-lg',
  [ButtonSize.ExtraLarge]: 'px-8 py-4 text-xl rounded-xl',
};

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/** Base Tailwind classes applied to every button / anchor element. */
const BASE_CLASSES =
  'inline-flex items-center justify-center gap-2 font-medium' +
  ' motion-safe:transition-colors motion-safe:duration-150' +
  ' focus:outline-none focus-visible:ring-2 focus-visible:ring-offset-2';

/**
 * General-purpose configurable button component.
 *
 * Replaces the monolith's `PcButton` ViewComponent and `<wv-button>` tag
 * helper. Renders either a `<button>` or an `<a>` element depending on the
 * `type` and `href` props, styled entirely with Tailwind CSS 4 utility
 * classes — zero Bootstrap or jQuery.
 *
 * @example
 * ```tsx
 * <Button color={ButtonColor.Primary} size={ButtonSize.Normal}>
 *   Save Changes
 * </Button>
 *
 * <Button
 *   type={ButtonType.Link}
 *   href="/dashboard"
 *   color={ButtonColor.Info}
 *   iconClass="fas fa-arrow-left"
 * >
 *   Back to Dashboard
 * </Button>
 * ```
 */
function Button({
  isVisible = true,
  type = ButtonType.Button,
  isOutline = false,
  isBlock = false,
  isActive = false,
  isDisabled = false,
  color = ButtonColor.White,
  size = ButtonSize.Inherit,
  className,
  id,
  text = 'button',
  onClick,
  href,
  newTab = false,
  iconClass,
  iconRight = false,
  form,
  children,
}: ButtonProps) {
  // ------------------------------------------------------------------
  // 1. Conditional visibility — matches monolith ViewBag.IsVisible
  // ------------------------------------------------------------------
  if (!isVisible) {
    return null;
  }

  // ------------------------------------------------------------------
  // 2. Resolve click handler
  // ------------------------------------------------------------------
  // Accept both React event handlers and legacy onclick strings.
  // String values are stored as a data-attribute; only functions are bound.
  const handleClick: MouseEventHandler<HTMLButtonElement | HTMLAnchorElement> | undefined =
    typeof onClick === 'function' ? onClick : undefined;

  const legacyOnClick: string | undefined =
    typeof onClick === 'string' && onClick.length > 0 ? onClick : undefined;

  // ------------------------------------------------------------------
  // 3. Build Tailwind class string
  // ------------------------------------------------------------------
  const colorClasses = isOutline
    ? OUTLINE_COLOR_CLASSES[color]
    : SOLID_COLOR_CLASSES[color];

  const sizeClasses = SIZE_CLASSES[size];
  const blockClass = isBlock ? 'w-full' : '';
  const activeClass = isActive ? 'ring-2 ring-offset-1' : '';
  const disabledClass = isDisabled
    ? 'opacity-50 cursor-not-allowed pointer-events-none'
    : '';

  const classes = [
    BASE_CLASSES,
    colorClasses,
    sizeClasses,
    blockClass,
    activeClass,
    disabledClass,
    className ?? '',
  ]
    .filter(Boolean)
    .join(' ');

  // ------------------------------------------------------------------
  // 4. Render content — icon + text / children
  // ------------------------------------------------------------------
  const iconElement = iconClass ? (
    <i className={iconClass} aria-hidden="true" />
  ) : null;

  // Children take precedence over the text prop.
  const textContent = children ?? (text ? <span>{text}</span> : null);

  const content = iconRight ? (
    <>
      {textContent}
      {iconElement}
    </>
  ) : (
    <>
      {iconElement}
      {textContent}
    </>
  );

  // ------------------------------------------------------------------
  // 5. Determine element type and render
  // ------------------------------------------------------------------
  // Render an <a> when the type is explicitly Link or when an href is
  // provided — matching Display.cshtml behaviour.
  const isLink = type === ButtonType.Link || (href != null && href !== '');

  if (isLink) {
    return (
      <a
        id={id}
        className={classes}
        href={href || '#'}
        target={newTab ? '_blank' : undefined}
        rel={newTab ? 'noopener noreferrer' : undefined}
        onClick={handleClick}
        aria-disabled={isDisabled || undefined}
        tabIndex={isDisabled ? -1 : undefined}
        data-onclick={legacyOnClick}
      >
        {content}
      </a>
    );
  }

  return (
    <button
      id={id}
      type={type === ButtonType.Submit ? 'submit' : 'button'}
      className={classes}
      disabled={isDisabled}
      onClick={handleClick}
      form={form || undefined}
      data-onclick={legacyOnClick}
    >
      {content}
    </button>
  );
}

export default Button;
