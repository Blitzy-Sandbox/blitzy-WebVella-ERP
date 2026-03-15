import React from 'react';

/**
 * Props for the LoadingSpinner component.
 *
 * @property size  - Size variant: 'sm' (16px), 'md' (32px), 'lg' (48px). Default: 'md'.
 * @property label - Optional label text displayed below the spinner.
 * @property className - Additional CSS classes applied to the wrapper div.
 */
export interface LoadingSpinnerProps {
  /** Size variant: sm (16px), md (32px), lg (48px). Default: 'md' */
  size?: 'sm' | 'md' | 'lg';
  /** Optional label text displayed below spinner */
  label?: string;
  /** Additional CSS classes for the wrapper div */
  className?: string;
}

/**
 * Tailwind CSS class map for each spinner size variant.
 *
 * - sm: 16×16px spinner with 2px border
 * - md: 32×32px spinner with 2px border (default)
 * - lg: 48×48px spinner with 4px border
 */
const SIZE_CLASSES: Record<string, string> = {
  sm: 'h-4 w-4 border-2',
  md: 'h-8 w-8 border-2',
  lg: 'h-12 w-12 border-4',
};

/**
 * LoadingSpinner — A reusable CSS-only loading indicator component.
 *
 * Renders a spinning circle built with Tailwind CSS `animate-spin` utility,
 * a circular border track (`border-gray-300`), and a coloured spinning
 * segment (`border-t-blue-600`).
 *
 * Accessibility:
 * - `role="status"` on the wrapper announces loading state to assistive
 *   technologies.
 * - A visually-hidden `<span className="sr-only">` provides a text
 *   alternative for screen readers, using the `label` prop value when
 *   supplied or the generic "Loading…" fallback.
 *
 * Used throughout the React SPA wherever asynchronous data fetching,
 * lazy-loaded component suspension, or other transient loading states
 * need a visual indicator — including Dashboard, SitePage, AppHome,
 * AppNode, and PageBodyNodeRenderer Suspense boundaries.
 */
export default function LoadingSpinner({
  size = 'md',
  label,
  className = '',
}: LoadingSpinnerProps) {
  return (
    <div
      className={`flex flex-col items-center justify-center ${className}`}
      role="status"
    >
      <div
        className={`animate-spin rounded-full border-gray-300 border-t-blue-600 ${SIZE_CLASSES[size]}`}
      />
      {label && (
        <p className="mt-2 text-sm text-gray-500">{label}</p>
      )}
      <span className="sr-only">{label || 'Loading...'}</span>
    </div>
  );
}
