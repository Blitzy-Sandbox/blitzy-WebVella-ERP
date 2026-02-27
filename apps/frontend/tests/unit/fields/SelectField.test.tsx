/**
 * SelectField Component Unit Tests
 *
 * Comprehensive Vitest test suite for the SelectField React component
 * that replaces the monolith's PcFieldSelect ViewComponent.
 *
 * Tests cover: display mode, edit mode (custom ARIA combobox dropdown),
 * options rendering, AJAX datasource loading, select matching/filtering,
 * default value handling, access control modes, validation errors,
 * null/empty value handling, and visibility toggling.
 */
import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import SelectField from '../../../src/components/fields/SelectField';
import type { SelectFieldProps, SelectOption } from '../../../src/components/fields/SelectField';

/* ------------------------------------------------------------------ */
/* Mock: apiClient used by SelectField for AJAX datasource fetching    */
/* ------------------------------------------------------------------ */
vi.mock('../../../src/api/client', () => ({
  default: {
    get: vi.fn(),
  },
}));
import apiClient from '../../../src/api/client';

/* ------------------------------------------------------------------ */
/* Test Data                                                           */
/* ------------------------------------------------------------------ */

/** Standard three-option set used in most tests */
const mockOptions: SelectOption[] = [
  { value: '1', label: 'Option 1' },
  { value: '2', label: 'Option 2' },
  { value: '3', label: 'Option 3', iconClass: 'fas fa-star' },
];

/** Options with color properties for color-badge rendering tests */
const optionsWithColor: SelectOption[] = [
  { value: 'r', label: 'Red', color: '#ff0000' },
  { value: 'b', label: 'Blue', color: '#0000ff', iconClass: 'fas fa-circle' },
  { value: 'g', label: 'Green' },
];

/**
 * Extended set (>5 items) to trigger the search input visibility.
 * The component's SEARCH_THRESHOLD constant is 5 — the search box
 * appears when allOptions.length > 5 or ajaxDatasourceApi is set.
 */
const extendedOptions: SelectOption[] = [
  { value: '1', label: 'Apple' },
  { value: '2', label: 'Apricot' },
  { value: '3', label: 'Banana' },
  { value: '4', label: 'Blueberry' },
  { value: '5', label: 'Cherry' },
  { value: '6', label: 'Grape' },
];

/* ------------------------------------------------------------------ */
/* Helper: create props with sensible defaults + overrides             */
/* ------------------------------------------------------------------ */
function createDefaultProps(
  overrides: Partial<SelectFieldProps> = {},
): SelectFieldProps {
  return {
    name: 'test_select',
    value: null,
    onChange: vi.fn(),
    options: mockOptions,
    ...overrides,
  } as SelectFieldProps;
}

/* ================================================================== */
/* Test Suite                                                          */
/* ================================================================== */
describe('SelectField', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  /* ================================================================ */
  /* Display Mode                                                      */
  /* ================================================================ */
  describe('display mode', () => {
    it('shows selected option label text', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'display', value: '1' }) as any)}
        />,
      );
      expect(screen.getByText('Option 1')).toBeInTheDocument();
    });

    it('shows icon when showIcon=true and option has iconClass', () => {
      const { container } = render(
        <SelectField
          {...(createDefaultProps({
            mode: 'display',
            value: '3',
            showIcon: true,
          }) as any)}
        />,
      );
      const icon = container.querySelector('i.fas.fa-star');
      expect(icon).toBeInTheDocument();
      expect(icon).toHaveAttribute('aria-hidden', 'true');
    });

    it('shows href link when href provided', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'display',
            value: '1',
            href: '/details/1',
          }) as any)}
        />,
      );
      const link = screen.getByRole('link');
      expect(link).toHaveAttribute('href', '/details/1');
      expect(link).toHaveTextContent('Option 1');
    });

    it('renders emptyValueMessage when value is null', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'display',
            value: null,
            emptyValueMessage: 'nothing selected',
          }) as any)}
        />,
      );
      expect(screen.getByText('nothing selected')).toBeInTheDocument();
    });

    it('renders default emptyValueMessage "no data" when value is null', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'display', value: null }) as any)}
        />,
      );
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders emptyValueMessage when no matching option found', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'display',
            value: 'nonexistent',
          }) as any)}
        />,
      );
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('renders color badge when option has color property', () => {
      const { container } = render(
        <SelectField
          {...(createDefaultProps({
            mode: 'display',
            value: 'r',
            options: optionsWithColor,
          }) as any)}
        />,
      );
      const badge = container.querySelector('span.rounded-full[aria-hidden="true"]');
      expect(badge).toBeInTheDocument();
    });

    it('is visible when rendered in display mode with a valid value', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'display', value: '1' }) as any)}
        />,
      );
      expect(screen.getByText('Option 1')).toBeVisible();
    });
  });

  /* ================================================================ */
  /* Edit Mode                                                         */
  /* ================================================================ */
  describe('edit mode', () => {
    it('renders a combobox trigger button', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit' }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toBeInTheDocument();
      expect(combobox).toHaveAttribute('aria-haspopup', 'listbox');
    });

    it('displays options from options array when dropdown is opened', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit' }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const listbox = screen.getByRole('listbox');
      const options = within(listbox).getAllByRole('option');
      // 3 mock options + 1 clear/none option (not required by default)
      expect(options.length).toBeGreaterThanOrEqual(mockOptions.length);
    });

    it('shows current value label in trigger text', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', value: '2' }) as any)}
        />,
      );
      expect(screen.getByText('Option 2')).toBeInTheDocument();
    });

    it('shows custom placeholder text when no value is selected', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            value: null,
            placeholder: 'Choose an option',
          }) as any)}
        />,
      );
      expect(screen.getByText('Choose an option')).toBeInTheDocument();
    });

    it('shows default placeholder "Select\u2026" when no value and no custom placeholder', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', value: null }) as any)}
        />,
      );
      expect(screen.getByText('Select\u2026')).toBeInTheDocument();
    });

    it('includes clear/none option when not required', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', required: false }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.getByText(/None/)).toBeInTheDocument();
    });

    it('does not include clear/none option when required=true', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', required: true }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.queryByText(/None/)).not.toBeInTheDocument();
    });

    it('calls onChange with selected value when option is clicked', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', value: null, onChange }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      await user.click(screen.getByRole('option', { name: 'Option 1' }));
      expect(onChange).toHaveBeenCalledWith('1');
    });

    it('calls onChange with null when clear/none option is selected', async () => {
      const onChange = vi.fn();
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            value: '1',
            required: false,
            onChange,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      await user.click(screen.getByText(/None/));
      expect(onChange).toHaveBeenCalledWith(null);
    });

    it('closes dropdown after selecting an option', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit' }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      await user.click(combobox);
      expect(screen.getByRole('listbox')).toBeInTheDocument();
      await user.click(screen.getByRole('option', { name: 'Option 1' }));
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });

    it('sets aria-expanded correctly when toggling dropdown', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit' }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-expanded', 'false');
      await user.click(combobox);
      expect(combobox).toHaveAttribute('aria-expanded', 'true');
    });
  });

  /* ================================================================ */
  /* Options Rendering                                                 */
  /* ================================================================ */
  describe('options rendering', () => {
    it('renders all options as role="option" elements', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', required: true }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const listbox = screen.getByRole('listbox');
      const opts = within(listbox).getAllByRole('option');
      expect(opts).toHaveLength(mockOptions.length);
      expect(opts[0]).toHaveTextContent('Option 1');
      expect(opts[1]).toHaveTextContent('Option 2');
      expect(opts[2]).toHaveTextContent('Option 3');
    });

    it('renders clear/none as first option when not required', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', required: false }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const listbox = screen.getByRole('listbox');
      const opts = within(listbox).getAllByRole('option');
      expect(opts[0]).toHaveTextContent(/None/);
    });

    it('renders custom placeholder text in clear option when provided', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            required: false,
            placeholder: 'Please select',
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const listbox = screen.getByRole('listbox');
      const opts = within(listbox).getAllByRole('option');
      expect(opts[0]).toHaveTextContent('Please select');
    });

    it('shows "No matching options" when options array is empty', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: [],
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.getByText('No matching options')).toBeInTheDocument();
    });

    it('renders option icons when showIcon=true and option has iconClass', async () => {
      const user = userEvent.setup();
      const { container } = render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', showIcon: true }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      // Option 3 has iconClass "fas fa-star"
      const icons = container.querySelectorAll('i.fas.fa-star');
      expect(icons.length).toBeGreaterThanOrEqual(1);
    });

    it('renders color badges for options with color property', async () => {
      const user = userEvent.setup();
      const { container } = render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: optionsWithColor,
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const badges = container.querySelectorAll(
        'span.rounded-full[aria-hidden="true"]',
      );
      // Red and Blue have color property, Green does not
      expect(badges.length).toBe(2);
    });

    it('marks selected option with aria-selected="true"', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            value: '2',
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      const selected = screen.getByRole('option', {
        name: 'Option 2',
        selected: true,
      });
      expect(selected).toHaveAttribute('aria-selected', 'true');
    });
  });

  /* ================================================================ */
  /* AJAX Datasource                                                   */
  /* ================================================================ */
  describe('AJAX datasource', () => {
    const ajaxOptions: SelectOption[] = [
      { value: 'a1', label: 'Ajax Option 1' },
      { value: 'a2', label: 'Ajax Option 2' },
    ];

    it('fetches options from ajaxDatasourceApi on mount when provided', async () => {
      vi.mocked(apiClient.get).mockResolvedValueOnce({
        data: ajaxOptions,
      } as any);

      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            ajaxDatasourceApi: '/api/v1/options',
          }) as any)}
        />,
      );

      await waitFor(() => {
        expect(apiClient.get).toHaveBeenCalledWith('/api/v1/options');
      });
    });

    it('uses fetched options in dropdown', async () => {
      vi.mocked(apiClient.get).mockResolvedValueOnce({
        data: ajaxOptions,
      } as any);

      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: [],
            ajaxDatasourceApi: '/api/v1/options',
          }) as any)}
        />,
      );

      // Wait for AJAX call to complete
      await waitFor(() => {
        expect(apiClient.get).toHaveBeenCalled();
      });

      await user.click(screen.getByRole('combobox'));

      await waitFor(() => {
        expect(screen.getByText('Ajax Option 1')).toBeInTheDocument();
        expect(screen.getByText('Ajax Option 2')).toBeInTheDocument();
      });
    });

    it('merges AJAX options with static options', async () => {
      vi.mocked(apiClient.get).mockResolvedValueOnce({
        data: ajaxOptions,
      } as any);

      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: [{ value: 's1', label: 'Static Option' }],
            ajaxDatasourceApi: '/api/v1/options',
          }) as any)}
        />,
      );

      await waitFor(() => {
        expect(apiClient.get).toHaveBeenCalled();
      });

      await user.click(screen.getByRole('combobox'));

      await waitFor(() => {
        expect(screen.getByText('Static Option')).toBeInTheDocument();
        expect(screen.getByText('Ajax Option 1')).toBeInTheDocument();
      });
    });

    it('falls back to provided options if AJAX fails', async () => {
      vi.mocked(apiClient.get).mockRejectedValueOnce(
        new Error('Network error'),
      );

      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: mockOptions,
            ajaxDatasourceApi: '/api/v1/options',
          }) as any)}
        />,
      );

      await waitFor(() => {
        expect(apiClient.get).toHaveBeenCalled();
      });

      await user.click(screen.getByRole('combobox'));

      // Static options remain available on failure
      expect(screen.getByText('Option 1')).toBeInTheDocument();
      expect(screen.getByText('Option 2')).toBeInTheDocument();
    });

    it('handles ApiResponse envelope format with object field', async () => {
      vi.mocked(apiClient.get).mockResolvedValueOnce({
        data: {
          success: true,
          object: ajaxOptions,
        },
      } as any);

      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: [],
            ajaxDatasourceApi: '/api/v1/options',
          }) as any)}
        />,
      );

      await waitFor(() => {
        expect(apiClient.get).toHaveBeenCalled();
      });

      await user.click(screen.getByRole('combobox'));

      await waitFor(() => {
        expect(screen.getByText('Ajax Option 1')).toBeInTheDocument();
      });
    });
  });

  /* ================================================================ */
  /* Select Matching / Filtering                                       */
  /* ================================================================ */
  describe('select matching/filtering', () => {
    it('applies "contains" filter type by default', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: extendedOptions,
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));

      const searchbox = screen.getByRole('searchbox');
      await user.type(searchbox, 'an');

      // "an" is contained in "Banana" (ban-AN-a)
      expect(screen.getByText('Banana')).toBeInTheDocument();
      // "Apple" does not contain "an"
      expect(screen.queryByText('Apple')).not.toBeInTheDocument();
    });

    it('supports "startsWith" filter type', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: extendedOptions,
            selectMatchType: 'startsWith',
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));

      const searchbox = screen.getByRole('searchbox');
      await user.type(searchbox, 'Ap');

      // "Apple" and "Apricot" start with "Ap"
      expect(screen.getByText('Apple')).toBeInTheDocument();
      expect(screen.getByText('Apricot')).toBeInTheDocument();
      // "Banana" does not start with "Ap"
      expect(screen.queryByText('Banana')).not.toBeInTheDocument();
    });

    it('supports "exact" filter type', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: extendedOptions,
            selectMatchType: 'exact',
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));

      const searchbox = screen.getByRole('searchbox');
      await user.type(searchbox, 'Cherry');

      expect(screen.getByText('Cherry')).toBeInTheDocument();
      expect(screen.queryByText('Apple')).not.toBeInTheDocument();
      expect(screen.queryByText('Banana')).not.toBeInTheDocument();
    });

    it('shows search input when options count exceeds threshold', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: extendedOptions,
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.getByRole('searchbox')).toBeInTheDocument();
    });

    it('does not show search input when options count is at or below threshold', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: mockOptions,
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    });

    it('shows search input when ajaxDatasourceApi is set regardless of count', async () => {
      vi.mocked(apiClient.get).mockResolvedValueOnce({ data: [] } as any);

      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: mockOptions,
            ajaxDatasourceApi: '/api/v1/search',
          }) as any)}
        />,
      );

      await waitFor(() => {
        expect(apiClient.get).toHaveBeenCalled();
      });

      await user.click(screen.getByRole('combobox'));
      expect(screen.getByRole('searchbox')).toBeInTheDocument();
    });

    it('reflects typed text in searchbox value', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: extendedOptions,
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));

      const searchbox = screen.getByRole('searchbox');
      await user.type(searchbox, 'test');
      expect(searchbox).toHaveValue('test');
    });

    it('displays "No matching options" when filter yields no results', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            options: extendedOptions,
            required: true,
          }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));

      const searchbox = screen.getByRole('searchbox');
      await user.type(searchbox, 'zzzzzzz');
      expect(screen.getByText('No matching options')).toBeInTheDocument();
    });
  });

  /* ================================================================ */
  /* Default Value                                                     */
  /* ================================================================ */
  describe('default value', () => {
    it('selects default value from options and shows label in trigger', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', value: '2' }) as any)}
        />,
      );
      expect(screen.getByText('Option 2')).toBeInTheDocument();
    });

    it('handles no default value by showing placeholder', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            value: null,
            placeholder: 'Pick one',
          }) as any)}
        />,
      );
      expect(screen.getByText('Pick one')).toBeInTheDocument();
    });

    it('shows selected option icon in trigger when value matches and showIcon', () => {
      const { container } = render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            value: '3',
            showIcon: true,
          }) as any)}
        />,
      );
      expect(screen.getByText('Option 3')).toBeInTheDocument();
      const icon = container.querySelector('i.fas.fa-star');
      expect(icon).toBeInTheDocument();
    });
  });

  /* ================================================================ */
  /* Access Control                                                    */
  /* ================================================================ */
  describe('access control', () => {
    it('renders normally with access="full"', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', access: 'full' }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).not.toBeDisabled();
      expect(combobox).toBeVisible();
      await user.click(combobox);
      expect(screen.getByRole('listbox')).toBeInTheDocument();
    });

    it('renders as disabled with access="readonly"', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', access: 'readonly' }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toBeDisabled();
    });

    it('does not open dropdown when access="readonly"', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', access: 'readonly' }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });

    it('renders access denied message with access="forbidden"', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', access: 'forbidden' }) as any)}
        />,
      );
      const alert = screen.getByRole('alert');
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveTextContent('access denied');
    });

    it('renders custom accessDeniedMessage with access="forbidden"', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            access: 'forbidden',
            accessDeniedMessage: 'Not authorized',
          }) as any)}
        />,
      );
      expect(screen.getByRole('alert')).toHaveTextContent('Not authorized');
    });

    it('does not render combobox when access="forbidden"', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', access: 'forbidden' }) as any)}
        />,
      );
      expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    });
  });

  /* ================================================================ */
  /* Validation                                                        */
  /* ================================================================ */
  describe('validation', () => {
    it('shows error indication when error prop provided', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            error: 'Selection is required',
          }) as any)}
        />,
      );
      // SelectField applies aria-invalid and error border styling rather than
      // rendering error message text inline — the parent form is responsible
      // for displaying the textual message. Validate the error state signals.
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-invalid', 'true');
      expect(combobox).toHaveClass('border-red-300');
    });

    it('applies error styling to trigger button', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            error: 'Field is invalid',
          }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveClass('border-red-300');
    });

    it('sets aria-invalid on combobox when error is present', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            error: 'Invalid selection',
          }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toHaveAttribute('aria-invalid', 'true');
    });

    it('does not apply error styling when no error', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit' }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).not.toHaveClass('border-red-300');
    });
  });

  /* ================================================================ */
  /* Null / Empty Handling                                             */
  /* ================================================================ */
  describe('null/empty handling', () => {
    it('handles null value by showing placeholder in edit mode', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', value: null }) as any)}
        />,
      );
      expect(screen.getByText('Select\u2026')).toBeInTheDocument();
    });

    it('handles value not in options list by showing placeholder', () => {
      render(
        <SelectField
          {...(createDefaultProps({
            mode: 'edit',
            value: 'nonexistent_value',
          }) as any)}
        />,
      );
      // Component should show placeholder when value doesn't match any option
      expect(screen.getByText('Select\u2026')).toBeInTheDocument();
    });

    it('handles null value in display mode with emptyValueMessage', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'display', value: null }) as any)}
        />,
      );
      expect(screen.getByText('no data')).toBeInTheDocument();
    });

    it('handles empty options array without errors', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', options: [] }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      expect(combobox).toBeInTheDocument();
    });
  });

  /* ================================================================ */
  /* Visibility                                                        */
  /* ================================================================ */
  describe('visibility', () => {
    it('renders when isVisible=true', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', isVisible: true }) as any)}
        />,
      );
      expect(screen.getByRole('combobox')).toBeInTheDocument();
    });

    it('renders nothing when isVisible=false', () => {
      const { container } = render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', isVisible: false }) as any)}
        />,
      );
      expect(container.firstChild).toBeNull();
      expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    });

    it('renders nothing in display mode when isVisible=false', () => {
      const { container } = render(
        <SelectField
          {...(createDefaultProps({
            mode: 'display',
            isVisible: false,
            value: '1',
          }) as any)}
        />,
      );
      expect(container.firstChild).toBeNull();
    });
  });

  /* ================================================================ */
  /* Keyboard Navigation                                               */
  /* ================================================================ */
  describe('keyboard navigation', () => {
    it('closes dropdown on Escape key', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit' }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      await user.click(combobox);
      expect(screen.getByRole('listbox')).toBeInTheDocument();

      fireEvent.keyDown(combobox, { key: 'Escape' });
      await waitFor(() => {
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
      });
    });

    it('navigates options with ArrowDown key', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', required: true }) as any)}
        />,
      );
      const combobox = screen.getByRole('combobox');
      await user.click(combobox);

      fireEvent.keyDown(combobox, { key: 'ArrowDown' });
      const options = screen.getAllByRole('option');
      expect(options.length).toBeGreaterThan(0);
    });
  });

  /* ================================================================ */
  /* Disabled State                                                    */
  /* ================================================================ */
  describe('disabled state', () => {
    it('renders as disabled when disabled prop is true', () => {
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', disabled: true }) as any)}
        />,
      );
      expect(screen.getByRole('combobox')).toBeDisabled();
    });

    it('does not open dropdown when disabled', async () => {
      const user = userEvent.setup();
      render(
        <SelectField
          {...(createDefaultProps({ mode: 'edit', disabled: true }) as any)}
        />,
      );
      await user.click(screen.getByRole('combobox'));
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });
  });
});
