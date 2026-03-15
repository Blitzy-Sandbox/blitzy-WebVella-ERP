/**
 * Vitest Component Tests for `<TabNav />`
 *
 * Validates the React TabNav component that replaces the monolith's
 * `PcTabNav` ViewComponent (`WebVella.Erp.Web/Components/PcTabNav/PcTabNav.cs`,
 * `Display-Tabs.cshtml`, `Display-Pills.cshtml`, `service.js`).
 *
 * The monolith supports up to 7 tabs in both Tabs (border-bottom) and Pills
 * (rounded buttons) render modes using Bootstrap nav-tabs / nav-pills with
 * jQuery-based tab switching (`$(this).tab('show')`). The React replacement
 * uses Tailwind CSS styling and native React state management.
 *
 * Test coverage includes:
 *  - Tab/pill render type styling (TABS border-bottom, PILLS rounded-full)
 *  - Visible tab count clamping (1–7), default of 2
 *  - Active tab state (first-tab default, controlled via activeTabId)
 *  - Tab switching (click handler replaces jQuery data-toggle="tab")
 *  - Content panel visibility (hidden class, not unmounted — Bootstrap behavior)
 *  - isVisible toggle (renders null when false)
 *  - CSS class passthrough (className, bodyClassName)
 *  - ARIA accessibility (tablist, tab, tabpanel, aria-selected, aria-controls,
 *    aria-labelledby)
 *  - Tab ID configuration from TabConfig
 *  - Edge cases (zero tabs, single tab, long labels)
 *
 * @see apps/frontend/src/components/common/TabNav.tsx
 * @see WebVella.Erp.Web/Components/PcTabNav/PcTabNav.cs
 * @see WebVella.Erp.Web/Components/PcTabNav/Display-Tabs.cshtml
 * @see WebVella.Erp.Web/Components/PcTabNav/Display-Pills.cshtml
 * @see WebVella.Erp.Web/Components/PcTabNav/service.js
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import TabNav, {
  TabNavRenderType,
  TabConfig,
  TabNavProps,
} from '../../../src/components/common/TabNav';

// ---------------------------------------------------------------------------
// Test Data Constants
// ---------------------------------------------------------------------------

/**
 * Default three-tab configuration used across most tests.
 * Mirrors the monolith's tab1/tab2/tab3 slots with default-style IDs.
 */
const defaultTabs: TabConfig[] = [
  { id: 'tab1', label: 'Tab 1', content: <div>Content 1</div> },
  { id: 'tab2', label: 'Tab 2', content: <div>Content 2</div> },
  { id: 'tab3', label: 'Tab 3', content: <div>Content 3</div> },
];

/**
 * Full seven-tab configuration matching the monolith's maximum tab count.
 * PcTabNavOptions defined tab1_id through tab7_id and tab1_label through
 * tab7_label (with defaults "tab1"–"tab7" and "Tab 1"–"Tab 7").
 */
const sevenTabs: TabConfig[] = Array.from({ length: 7 }, (_, i) => ({
  id: `tab${i + 1}`,
  label: `Tab ${i + 1}`,
  content: <div>Content {i + 1}</div>,
}));

/**
 * Ten-tab configuration to test the 7-tab maximum clamp.
 */
const tenTabs: TabConfig[] = Array.from({ length: 10 }, (_, i) => ({
  id: `tab${i + 1}`,
  label: `Tab ${i + 1}`,
  content: <div>Content {i + 1}</div>,
}));

// ---------------------------------------------------------------------------
// 2.1 Tab/Pill Navigation Render Types
// ---------------------------------------------------------------------------

describe('TabNav — Render Types', () => {
  it('renders with PILLS render type by default', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    // PILLS default: tab buttons should have rounded-full class
    const tabs = screen.getAllByRole('tab');
    expect(tabs.length).toBeGreaterThanOrEqual(1);

    // The first tab is active by default and should have pill-active styles
    expect(tabs[0].className).toContain('rounded-full');
    expect(tabs[0].className).toContain('bg-blue-600');
  });

  it('renders with TABS render type', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        renderType={TabNavRenderType.TABS}
      />,
    );

    // TABS: the tablist container (<ul>) should have border-b class
    const tablist = screen.getByRole('tablist');
    expect(tablist.className).toContain('border-b');
    expect(tablist.className).toContain('border-gray-200');

    // Active tab should have border-bottom indicator, NOT pill bg
    const tabs = screen.getAllByRole('tab');
    expect(tabs[0].className).toContain('border-b-2');
    expect(tabs[0].className).toContain('border-blue-600');
    expect(tabs[0].className).not.toContain('rounded-full');
  });

  it('PILLS style: active tab has filled background', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        renderType={TabNavRenderType.PILLS}
      />,
    );

    const tabs = screen.getAllByRole('tab');
    // First tab is active by default
    expect(tabs[0].className).toContain('bg-blue-600');
    expect(tabs[0].className).toContain('text-white');
  });

  it('TABS style: active tab has border-bottom indicator', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        renderType={TabNavRenderType.TABS}
      />,
    );

    const tabs = screen.getAllByRole('tab');
    expect(tabs[0].className).toContain('border-b-2');
    expect(tabs[0].className).toContain('border-blue-600');
    expect(tabs[0].className).toContain('text-blue-600');
  });

  it('PILLS style: inactive tabs have transparent background', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        renderType={TabNavRenderType.PILLS}
      />,
    );

    const tabs = screen.getAllByRole('tab');
    // Second and third tabs are inactive
    expect(tabs[1].className).not.toContain('bg-blue-600');
    expect(tabs[1].className).toContain('text-gray-600');
    expect(tabs[2].className).not.toContain('bg-blue-600');
    expect(tabs[2].className).toContain('text-gray-600');
  });

  it('TABS style: inactive tabs have transparent border', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        renderType={TabNavRenderType.TABS}
      />,
    );

    const tabs = screen.getAllByRole('tab');
    // Inactive tabs should have border-transparent
    expect(tabs[1].className).toContain('border-transparent');
    expect(tabs[2].className).toContain('border-transparent');
    // Should NOT have the blue border
    expect(tabs[1].className).not.toContain('border-blue-600');
  });
});

// ---------------------------------------------------------------------------
// 2.2 Up to 7 Tab Containers (from PcTabNavOptions.VisibleTabs)
// ---------------------------------------------------------------------------

describe('TabNav — Visible Tab Count', () => {
  it('renders correct number of tabs based on visibleTabs', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(3);
  });

  it('default visibleTabs is 2', () => {
    // Pass 5 tabs without specifying visibleTabs
    // PcTabNavOptions.VisibleTabs defaulted to 2
    const fiveTabs: TabConfig[] = Array.from({ length: 5 }, (_, i) => ({
      id: `tab${i + 1}`,
      label: `Tab ${i + 1}`,
      content: <div>Content {i + 1}</div>,
    }));

    render(<TabNav tabs={fiveTabs} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(2);
  });

  it('renders 1 tab when visibleTabs is 1', () => {
    render(<TabNav tabs={sevenTabs} visibleTabs={1} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(1);
    expect(tabs[0]).toHaveTextContent('Tab 1');
  });

  it('renders maximum 7 tabs even when visibleTabs exceeds 7', () => {
    // Pass 10 tabs with visibleTabs=10 — component should clamp to MAX_TABS=7
    render(<TabNav tabs={tenTabs} visibleTabs={10} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(7);
  });

  it('renders all 7 tabs when visibleTabs is 7', () => {
    render(<TabNav tabs={sevenTabs} visibleTabs={7} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(7);

    // Verify all labels are present
    for (let i = 1; i <= 7; i++) {
      expect(screen.getByText(`Tab ${i}`)).toBeInTheDocument();
    }
  });

  it('tab labels match tab configuration', () => {
    const customTabs: TabConfig[] = [
      { id: 'alpha', label: 'Alpha Tab', content: <div>A</div> },
      { id: 'beta', label: 'Beta Tab', content: <div>B</div> },
      { id: 'gamma', label: 'Gamma Tab', content: <div>C</div> },
    ];

    render(<TabNav tabs={customTabs} visibleTabs={3} />);

    expect(screen.getByText('Alpha Tab')).toBeInTheDocument();
    expect(screen.getByText('Beta Tab')).toBeInTheDocument();
    expect(screen.getByText('Gamma Tab')).toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// 2.3 Active Tab State Tests
// ---------------------------------------------------------------------------

describe('TabNav — Active Tab State', () => {
  it('first tab is active by default', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');

    // First tab should be active
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');

    // Its content panel should be visible
    const panels = screen.getAllByRole('tabpanel');
    const firstPanel = panels.find((p) =>
      p.getAttribute('aria-labelledby')?.includes('tab1'),
    );
    expect(firstPanel).toBeDefined();
    expect(firstPanel!.className).toContain('block');
  });

  it('non-first tabs are inactive by default', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');
    // Second and third tabs should be inactive
    expect(tabs[1]).toHaveAttribute('aria-selected', 'false');
    expect(tabs[2]).toHaveAttribute('aria-selected', 'false');

    // Their content panels should be hidden
    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    const hiddenPanels = panels.filter((p) => p.className.includes('hidden'));
    expect(hiddenPanels.length).toBe(2);
  });

  it('controlled mode: activeTabId sets active tab', () => {
    render(
      <TabNav tabs={defaultTabs} visibleTabs={3} activeTabId="tab2" />,
    );

    const tabs = screen.getAllByRole('tab');
    // Tab 2 should be active
    expect(tabs[1]).toHaveAttribute('aria-selected', 'true');
    // Tab 1 should be inactive
    expect(tabs[0]).toHaveAttribute('aria-selected', 'false');
  });

  it('controlled mode: changing activeTabId switches tab', () => {
    const { rerender } = render(
      <TabNav tabs={defaultTabs} visibleTabs={3} activeTabId="tab1" />,
    );

    // Tab 1 is initially active
    let tabs = screen.getAllByRole('tab');
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');

    // Re-render with activeTabId="tab3"
    rerender(
      <TabNav tabs={defaultTabs} visibleTabs={3} activeTabId="tab3" />,
    );

    tabs = screen.getAllByRole('tab');
    // Tab 3 should now be active
    expect(tabs[2]).toHaveAttribute('aria-selected', 'true');
    // Tab 1 should be inactive
    expect(tabs[0]).toHaveAttribute('aria-selected', 'false');
  });
});

// ---------------------------------------------------------------------------
// 2.4 Tab Switching Tests
// ---------------------------------------------------------------------------

describe('TabNav — Tab Switching', () => {
  it('clicking a tab makes it active', async () => {
    const user = userEvent.setup();
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');
    // Click Tab 2
    await user.click(tabs[1]);

    // Tab 2 should now be active
    expect(tabs[1]).toHaveAttribute('aria-selected', 'true');
    // Tab 1 should be inactive
    expect(tabs[0]).toHaveAttribute('aria-selected', 'false');
  });

  it('clicking a tab shows its content panel', async () => {
    const user = userEvent.setup();
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    // Initially Content 1 should be visible
    const allPanels = screen.getAllByRole('tabpanel', { hidden: true });
    expect(allPanels[0].className).toContain('block');
    expect(allPanels[1].className).toContain('hidden');

    // Click Tab 2
    const tabs = screen.getAllByRole('tab');
    await user.click(tabs[1]);

    // Content 2 should now be visible, Content 1 hidden
    expect(allPanels[0].className).toContain('hidden');
    expect(allPanels[1].className).toContain('block');
  });

  it('onTabChange callback is called with tab id', async () => {
    const user = userEvent.setup();
    const onTabChange = vi.fn();

    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        onTabChange={onTabChange}
      />,
    );

    const tabs = screen.getAllByRole('tab');
    await user.click(tabs[1]);

    expect(onTabChange).toHaveBeenCalledTimes(1);
    expect(onTabChange).toHaveBeenCalledWith('tab2');
  });

  it('clicking already active tab still triggers callback', async () => {
    // The component's handleTabClick always invokes onTabChange,
    // regardless of whether the clicked tab is already active.
    const user = userEvent.setup();
    const onTabChange = vi.fn();

    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        onTabChange={onTabChange}
      />,
    );

    const tabs = screen.getAllByRole('tab');
    // Tab 1 is active by default — click it again
    await user.click(tabs[0]);

    expect(onTabChange).toHaveBeenCalledTimes(1);
    expect(onTabChange).toHaveBeenCalledWith('tab1');
  });

  it('rapid tab switching settles on last clicked', async () => {
    const user = userEvent.setup();
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');

    // Rapid switch: Tab 1 → Tab 2 → Tab 3
    await user.click(tabs[0]);
    await user.click(tabs[2]);

    // Tab 3 should be the final active tab
    expect(tabs[2]).toHaveAttribute('aria-selected', 'true');
    expect(tabs[0]).toHaveAttribute('aria-selected', 'false');
    expect(tabs[1]).toHaveAttribute('aria-selected', 'false');
  });
});

// ---------------------------------------------------------------------------
// 2.5 Content Panel Rendering
// ---------------------------------------------------------------------------

describe('TabNav — Content Panel Rendering', () => {
  it('active tab content is visible', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    // First tab's content should be visible (block class)
    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    expect(panels[0].className).toContain('block');
    expect(panels[0].className).not.toContain('hidden');
  });

  it('inactive tab content is hidden but in DOM', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    // All panels should be in the DOM (hidden panels use display:none via
    // the "hidden" CSS class, matching Bootstrap's behavior of keeping
    // inactive tab panes mounted for state preservation)
    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    expect(panels).toHaveLength(3);

    // Only the first should be visible; others should be hidden
    expect(panels[0].className).toContain('block');
    expect(panels[1].className).toContain('hidden');
    expect(panels[2].className).toContain('hidden');
  });

  it('each tab panel has correct content', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const panels = screen.getAllByRole('tabpanel', { hidden: true });

    // Each panel should contain its specific content
    expect(within(panels[0]).getByText('Content 1')).toBeInTheDocument();
    expect(within(panels[1]).getByText('Content 2')).toBeInTheDocument();
    expect(within(panels[2]).getByText('Content 3')).toBeInTheDocument();
  });

  it('tab content changes when switching tabs', async () => {
    const user = userEvent.setup();
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    const tabs = screen.getAllByRole('tab');

    // Initially Content 1 visible
    expect(panels[0].className).toContain('block');
    expect(panels[1].className).toContain('hidden');

    // Click Tab 2
    await user.click(tabs[1]);

    // Content 2 visible, Content 1 hidden
    expect(panels[0].className).toContain('hidden');
    expect(panels[1].className).toContain('block');

    // Click Tab 3
    await user.click(tabs[2]);

    // Content 3 visible, others hidden
    expect(panels[0].className).toContain('hidden');
    expect(panels[1].className).toContain('hidden');
    expect(panels[2].className).toContain('block');
  });
});

// ---------------------------------------------------------------------------
// 2.6 Visibility Tests (from PcTabNavOptions.IsVisible)
// ---------------------------------------------------------------------------

describe('TabNav — Visibility', () => {
  it('renders nothing when isVisible is false', () => {
    // PcTabNav.cs line 157: returns Content("") when isVisible is false
    const { container } = render(
      <TabNav tabs={defaultTabs} visibleTabs={3} isVisible={false} />,
    );

    // Component returns null — container should be empty
    expect(container.innerHTML).toBe('');
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
  });

  it('renders normally when isVisible is true', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} isVisible={true} />);

    expect(screen.getByRole('tablist')).toBeInTheDocument();
    expect(screen.getAllByRole('tab')).toHaveLength(3);
  });
});

// ---------------------------------------------------------------------------
// 2.7 CSS Class Tests (from PcTabNavOptions.CssClass and BodyCssClass)
// ---------------------------------------------------------------------------

describe('TabNav — CSS Class Passthrough', () => {
  it('applies className to nav wrapper', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        className="custom-nav-class"
      />,
    );

    const tablist = screen.getByRole('tablist');
    expect(tablist.className).toContain('custom-nav-class');
  });

  it('applies bodyClassName to content area', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        bodyClassName="custom-body-class"
      />,
    );

    // The body wrapper is the div that contains all tab panels.
    // It sits after the <ul> tablist and has the mt-2 base + bodyClassName.
    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    // The parent of the panels is the body wrapper
    const bodyWrapper = panels[0].parentElement!;
    expect(bodyWrapper.className).toContain('custom-body-class');
  });

  it('classes are additive, not replacing', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        className="extra-nav"
        bodyClassName="extra-body"
      />,
    );

    const tablist = screen.getByRole('tablist');
    // Should have base classes AND the custom class
    expect(tablist.className).toContain('flex');
    expect(tablist.className).toContain('extra-nav');

    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    const bodyWrapper = panels[0].parentElement!;
    expect(bodyWrapper.className).toContain('mt-2');
    expect(bodyWrapper.className).toContain('extra-body');
  });
});

// ---------------------------------------------------------------------------
// 2.8 Accessibility Tests (ARIA attributes)
// ---------------------------------------------------------------------------

describe('TabNav — Accessibility (ARIA)', () => {
  it('tab list has role="tablist"', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tablist = screen.getByRole('tablist');
    expect(tablist).toBeInTheDocument();
    expect(tablist.tagName.toLowerCase()).toBe('ul');
  });

  it('each tab button has role="tab"', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(3);

    tabs.forEach((tab) => {
      expect(tab.tagName.toLowerCase()).toBe('button');
      expect(tab).toHaveAttribute('role', 'tab');
    });
  });

  it('active tab has aria-selected="true"', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');
  });

  it('inactive tabs have aria-selected="false"', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs[1]).toHaveAttribute('aria-selected', 'false');
    expect(tabs[2]).toHaveAttribute('aria-selected', 'false');
  });

  it('each tab panel has role="tabpanel"', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    // Need to include hidden panels in the query
    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    expect(panels).toHaveLength(3);

    panels.forEach((panel) => {
      expect(panel).toHaveAttribute('role', 'tabpanel');
    });
  });

  it('tab aria-controls matches panel id', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');
    const panels = screen.getAllByRole('tabpanel', { hidden: true });

    tabs.forEach((tab, index) => {
      const ariaControls = tab.getAttribute('aria-controls');
      expect(ariaControls).toBeTruthy();

      // The matching panel should have this id
      const matchingPanel = panels[index];
      expect(matchingPanel.id).toBe(ariaControls);
    });
  });

  it('panel aria-labelledby matches tab id', () => {
    render(<TabNav tabs={defaultTabs} visibleTabs={3} />);

    const tabs = screen.getAllByRole('tab');
    const panels = screen.getAllByRole('tabpanel', { hidden: true });

    panels.forEach((panel, index) => {
      const ariaLabelledBy = panel.getAttribute('aria-labelledby');
      expect(ariaLabelledBy).toBeTruthy();

      // The matching tab button should have this id
      const matchingTab = tabs[index];
      expect(matchingTab.id).toBe(ariaLabelledBy);
    });
  });

  it('unique IDs across multiple TabNav instances', () => {
    // The monolith used "wv-tab-@node.Id-" prefix via useId() to
    // prevent ID conflicts when multiple TabNav instances coexist
    render(
      <div>
        <TabNav
          tabs={[
            { id: 'a', label: 'A', content: <div>A Content</div> },
            { id: 'b', label: 'B', content: <div>B Content</div> },
          ]}
          visibleTabs={2}
        />
        <TabNav
          tabs={[
            { id: 'a', label: 'A', content: <div>A2 Content</div> },
            { id: 'b', label: 'B', content: <div>B2 Content</div> },
          ]}
          visibleTabs={2}
        />
      </div>,
    );

    // Collect all tab button IDs
    const allTabs = screen.getAllByRole('tab');
    expect(allTabs).toHaveLength(4);

    const tabIds = allTabs.map((t) => t.id);
    const uniqueIds = new Set(tabIds);
    // Each tab should have a unique ID (no collisions)
    expect(uniqueIds.size).toBe(4);

    // Collect all panel IDs
    const allPanels = screen.getAllByRole('tabpanel', { hidden: true });
    expect(allPanels).toHaveLength(4);

    const panelIds = allPanels.map((p) => p.id);
    const uniquePanelIds = new Set(panelIds);
    expect(uniquePanelIds.size).toBe(4);
  });
});

// ---------------------------------------------------------------------------
// 2.9 Tab IDs from Configuration
// ---------------------------------------------------------------------------

describe('TabNav — Tab IDs from Configuration', () => {
  it('tab panels use configured IDs in their attributes', () => {
    const customTabs: TabConfig[] = [
      { id: 'custom-alpha', label: 'Alpha', content: <div>A</div> },
      { id: 'custom-beta', label: 'Beta', content: <div>B</div> },
    ];

    render(<TabNav tabs={customTabs} visibleTabs={2} />);

    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    // Panel IDs should contain the configured tab ID
    expect(panels[0].id).toContain('custom-alpha');
    expect(panels[1].id).toContain('custom-beta');
  });

  it('tab buttons use derived IDs with -tab suffix', () => {
    const customTabs: TabConfig[] = [
      { id: 'my-tab', label: 'My Tab', content: <div>Content</div> },
    ];

    render(<TabNav tabs={customTabs} visibleTabs={1} />);

    const tabButton = screen.getByRole('tab');
    // Button ID format: {instanceId}-{tabId}-tab
    expect(tabButton.id).toContain('my-tab');
    expect(tabButton.id).toMatch(/-tab$/);
  });
});

// ---------------------------------------------------------------------------
// 2.10 Edge Cases
// ---------------------------------------------------------------------------

describe('TabNav — Edge Cases', () => {
  it('handles zero tabs gracefully', () => {
    // When tabs array is empty, component returns null
    const { container } = render(<TabNav tabs={[]} visibleTabs={3} />);

    // Container should be empty since the component returns null
    expect(container.innerHTML).toBe('');
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
  });

  it('handles single tab', () => {
    const singleTab: TabConfig[] = [
      { id: 'only', label: 'Only Tab', content: <div>Only Content</div> },
    ];

    render(<TabNav tabs={singleTab} visibleTabs={1} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(1);
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('Only Content')).toBeInTheDocument();
  });

  it('very long tab labels render correctly', () => {
    const longLabelTabs: TabConfig[] = [
      {
        id: 'long1',
        label: 'This is an extremely long tab label that should still render properly',
        content: <div>Long Label Content</div>,
      },
      {
        id: 'long2',
        label: 'Another very long label for testing purposes to verify no crash',
        content: <div>Long Label Content 2</div>,
      },
    ];

    render(<TabNav tabs={longLabelTabs} visibleTabs={2} />);

    // Verify long labels render without errors
    expect(
      screen.getByText(
        'This is an extremely long tab label that should still render properly',
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        'Another very long label for testing purposes to verify no crash',
      ),
    ).toBeInTheDocument();
  });

  it('falls back to first tab when activeTabId does not match any tab', () => {
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        activeTabId="nonexistent-tab"
      />,
    );

    // Should fall back to first visible tab
    const tabs = screen.getAllByRole('tab');
    expect(tabs[0]).toHaveAttribute('aria-selected', 'true');
  });

  it('clamps visibleTabs below minimum to 1', () => {
    // visibleTabs = 0 or negative should be clamped to MIN_TABS = 1
    render(<TabNav tabs={defaultTabs} visibleTabs={0} />);

    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(1);
  });

  it('renders children alongside tab panels when provided', () => {
    render(
      <TabNav tabs={defaultTabs} visibleTabs={2}>
        <div data-testid="extra-child">Extra Content</div>
      </TabNav>,
    );

    expect(screen.getByTestId('extra-child')).toBeInTheDocument();
    expect(screen.getByText('Extra Content')).toBeInTheDocument();
  });

  it('tab switching works correctly with fireEvent.click', () => {
    const onTabChange = vi.fn();
    render(
      <TabNav
        tabs={defaultTabs}
        visibleTabs={3}
        onTabChange={onTabChange}
      />,
    );

    const tabs = screen.getAllByRole('tab');

    // Use fireEvent instead of userEvent for synchronous test
    fireEvent.click(tabs[2]);

    expect(tabs[2]).toHaveAttribute('aria-selected', 'true');
    expect(onTabChange).toHaveBeenCalledWith('tab3');
  });

  it('handles tabs with no content gracefully', () => {
    const noContentTabs: TabConfig[] = [
      { id: 'empty1', label: 'Empty 1' },
      { id: 'empty2', label: 'Empty 2' },
    ];

    render(<TabNav tabs={noContentTabs} visibleTabs={2} />);

    // Should render tab buttons without error
    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(2);

    // Panels exist but have no content nodes
    const panels = screen.getAllByRole('tabpanel', { hidden: true });
    expect(panels).toHaveLength(2);
  });
});
