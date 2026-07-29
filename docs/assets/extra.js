/*
  WebVella ERP TechDocs — rendered-site accessibility & search-UX behaviour.

  Registered via `extra_javascript` in mkdocs.yml. Additive, theme-level progressive
  enhancement over the Material theme (configured internally by techdocs-core); no
  Material source or template is modified (Rule C, minimal change).

  This build does NOT enable Material's `navigation.instant` feature (mkdocs.yml
  `theme.features` = navigation.footer + content.action.edit only), so every route is a
  full page load. That means this script runs once per navigation on a fresh document —
  there is no client-side view swap to re-bind to — so a single DOMContentLoaded setup
  with document-level (delegated) listeners is sufficient and safe.

  Findings addressed here:
    * PF-010 — the search dialog (role="dialog") has no accessible name site-wide, and
               focus is not returned to the search trigger when the dialog is dismissed.
               Name the dialog and restore focus on close.
    * PF-011 — the off-canvas mobile/tablet navigation drawer, when CLOSED, leaves its
               links keyboard-focusable and, when OPEN, has no focus trap / Escape-close /
               scroll-lock / focus-restore. Make it behave as a modal — but ONLY at the
               widths where it is actually an off-canvas drawer, so the persistent desktop
               sidebar navigation is never altered.
    * PF-016 — clearing the search query leaves stale result/status text, and the (then
               invisible) clear control keeps an accessible name. Reset the result/status
               region on clear and remove the empty clear control from the a11y tree.
    * PF-028 — a retained long query must not survive a close and widen the document; clear
               the query when the dialog closes (complemented by the CSS guard in extra.css).
*/
(function () {
  "use strict";

  /* Material renders the primary sidebar as an off-canvas *drawer* only below its own
     76.25em breakpoint; at or above it, the sidebar is persistent, non-modal navigation.
     All drawer-modality behaviour below is gated on this media query so desktop is never
     touched. 76.1875em == 1219px (one pixel below 76.25em/1220px). */
  var DRAWER_MEDIA = "(max-width: 76.1875em)";
  var mq = window.matchMedia(DRAWER_MEDIA);
  function inDrawerMode() { return mq.matches; }

  function el(sel, root) { return (root || document).querySelector(sel); }
  function els(sel, root) {
    return Array.prototype.slice.call((root || document).querySelectorAll(sel));
  }

  /* ============================ PF-010 — name the search dialog ============================ */
  function nameSearchDialog() {
    var dialog = el('.md-search[role="dialog"]') || el('.md-search[data-md-component="search"]');
    if (dialog && !dialog.getAttribute("aria-label") && !dialog.getAttribute("aria-labelledby")) {
      dialog.setAttribute("aria-label", "Search");
    }
  }

  /* ==================== PF-016 — clear-control a11y semantics + status reset ==================== */
  // The reset ("Clear") button is present in the DOM at all times but only visible when the
  // query is non-empty. When empty it must not be exposed to assistive technology.
  function syncClearButton() {
    var input = el("input.md-search__input");
    var clearBtn = el('.md-search__options button[type="reset"]');
    if (!input || !clearBtn) { return; }
    if (input.value.length === 0) {
      clearBtn.setAttribute("aria-hidden", "true");
      clearBtn.setAttribute("disabled", "");
    } else {
      clearBtn.removeAttribute("aria-hidden");
      clearBtn.removeAttribute("disabled");
    }
  }

  // Reset the result list and the status/meta line (which otherwise retains stale text such
  // as "No matching documents" after a clear, because a native form reset does not fire an
  // `input` event on the query field that Material's search pipeline listens to).
  function resetSearchOutput() {
    var meta = el(".md-search-result__meta");
    var list = el(".md-search-result__list");
    if (meta) { meta.textContent = ""; }
    if (list) { list.innerHTML = ""; }
  }

  /* ============ PF-028 (+ PF-010/PF-016) — clear retained query & output on close ============ */
  function clearSearchState() {
    var input = el("input.md-search__input");
    if (input) {
      input.value = "";
      // Let Material's reactive search pipeline observe the now-empty query.
      input.dispatchEvent(new Event("input", { bubbles: true }));
    }
    resetSearchOutput();
    syncClearButton();
  }

  /* ---------- PF-010 focus return: focus the VISIBLE search trigger on dismiss ----------
     The search trigger differs by breakpoint: at desktop widths the persistent header search
     box shows the inline magnifier `label.md-search__icon[for="__search"]`; at mobile/tablet
     widths the collapsed box is replaced by the header button
     `label.md-header__button[for="__search"]` (the desktop magnifier is then hidden, and vice
     versa). We focus whichever trigger is actually visible so focus is never dropped to <body>
     (WCAG SC 2.4.3 Focus Order). Deferred to the next animation frame so the close transition's
     display/visibility state has settled before we test visibility. */
  function isVisible(elm) {
    if (!elm || !document.contains(elm)) { return false; }
    var s = window.getComputedStyle(elm);
    if (s.display === "none" || s.visibility === "hidden") { return false; }
    var r = elm.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  }

  function visibleSearchTrigger() {
    var cands = [
      el('label.md-search__icon[for="__search"]'),   // desktop persistent-box magnifier
      el('label.md-header__button[for="__search"]')   // mobile/tablet header search button
    ];
    for (var i = 0; i < cands.length; i++) {
      if (isVisible(cands[i])) { return cands[i]; }
    }
    return cands[0] || cands[1] || null;
  }

  function restoreSearchFocus() {
    window.requestAnimationFrame(function () {
      var target = visibleSearchTrigger();
      if (!target) { return; }
      // A <label> is not focusable by default; make the trigger programmatically focusable.
      if (target.tagName === "LABEL" && (target.tabIndex === undefined || target.tabIndex < 0)) {
        target.tabIndex = 0;
      }
      try { target.focus({ preventScroll: true }); } catch (err) { /* no-op */ }
    });
  }

  /* ---------- PF-028 helper: clear the stale horizontal scroll extent left after a mobile
     search overlay open/close. At narrow widths, opening the full-screen search overlay and
     then dismissing it can leave the document with a stale horizontal scroll extent sourced
     from a wide in-content element (e.g. a long code line inside an overflow-x:auto block).
     The extent persists through passive waits and only clears on a forced reflow, so we
     toggle overflow-x to force the browser to recompute it. Idempotent and cheap. ---------- */
  function forceReflowResetHScroll() {
    var de = document.documentElement;
    var b = document.body;
    var pde = de.style.overflowX;
    var pb = b.style.overflowX;
    de.style.overflowX = "hidden";
    b.style.overflowX = "hidden";
    void de.offsetWidth; // synchronous reflow with clipped overflow
    de.style.overflowX = pde;
    b.style.overflowX = pb;
    void de.offsetWidth; // synchronous reflow after restore
  }

  function scheduleOverflowFix() {
    // Run across the close-transition window (Material's search transition is ~250ms) so the
    // recompute lands whenever the stale extent appears: now, next paint, mid- and post-transition.
    forceReflowResetHScroll();
    window.requestAnimationFrame(forceReflowResetHScroll);
    window.setTimeout(forceReflowResetHScroll, 60);
    window.setTimeout(forceReflowResetHScroll, 320);
  }

  function wireSearch() {
    var toggle = document.getElementById("__search");
    var input = el("input.md-search__input");
    var form = el("form.md-search__form");

    if (input) {
      input.addEventListener("input", syncClearButton);
    }

    if (form) {
      // The Clear button is type="reset"; a native reset empties the field WITHOUT firing an
      // `input` event, so re-dispatch one (after the reset applies) and reset the output.
      form.addEventListener("reset", function () {
        window.setTimeout(function () {
          resetSearchOutput();
          syncClearButton();
          var q = el("input.md-search__input");
          if (q) { q.dispatchEvent(new Event("input", { bubbles: true })); }
        }, 0);
      });
    }

    if (toggle) {
      toggle.addEventListener("change", function () {
        if (toggle.checked) {
          // Opened.
          nameSearchDialog();
          syncClearButton();
        } else {
          // Dismissed (Escape / overlay / toggle): clear state so no long query is retained
          // (PF-028) and no stale results linger (PF-016), restore focus to the visible search
          // trigger (PF-010), and clear any stale horizontal scroll extent left by the mobile
          // overlay close (PF-028).
          clearSearchState();
          restoreSearchFocus();
          scheduleOverflowFix();
        }
      });
    }

    // Initial per-page state.
    nameSearchDialog();
    syncClearButton();
  }

  /* ================================ PF-011 — drawer modality ================================ */
  function primarySidebar() { return el(".md-sidebar--primary"); }
  function drawerToggle() { return document.getElementById("__drawer"); }
  function drawerTrigger() { return el('label.md-header__button[for="__drawer"]'); }

  var scrollLocked = false;
  function lockScroll(lock) {
    if (lock === scrollLocked) { return; }
    scrollLocked = lock;
    document.documentElement.style.overflow = lock ? "hidden" : "";
    document.body.style.overflow = lock ? "hidden" : "";
  }

  function setDrawerInert(inert) {
    var sb = primarySidebar();
    if (!sb) { return; }
    if (inert) {
      sb.setAttribute("inert", "");
      sb.setAttribute("aria-hidden", "true");
    } else {
      sb.removeAttribute("inert");
      sb.removeAttribute("aria-hidden");
    }
  }

  function drawerFocusables() {
    var sb = primarySidebar();
    if (!sb) { return []; }
    return els(
      'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      sb
    ).filter(function (n) {
      return n.offsetParent !== null || n === document.activeElement;
    });
  }

  function restoreDrawerFocus() {
    var trigger = drawerTrigger();
    if (trigger) {
      if (trigger.tabIndex === undefined || trigger.tabIndex < 0) { trigger.tabIndex = 0; }
      try { trigger.focus({ preventScroll: true }); } catch (err) { /* no-op */ }
    }
  }

  function syncDrawer() {
    var toggle = drawerToggle();
    if (!toggle) { return; }

    if (!inDrawerMode()) {
      // Desktop: the primary sidebar is persistent navigation — it must remain fully
      // interactive and in the a11y tree, with no scroll lock. This is the desktop-safety guard.
      setDrawerInert(false);
      lockScroll(false);
      return;
    }

    if (toggle.checked) {
      // Open drawer → interactive + modal.
      setDrawerInert(false);
      lockScroll(true);
      var sb = primarySidebar();
      if (sb && (!document.activeElement || !sb.contains(document.activeElement))) {
        var f = drawerFocusables();
        if (f.length) {
          try { f[0].focus({ preventScroll: true }); } catch (err) { /* no-op */ }
        }
      }
    } else {
      // Closed drawer → inert (its links are off-canvas but still rendered via transform,
      // so without `inert` they stay keyboard-focusable — the PF-011 defect).
      setDrawerInert(true);
      lockScroll(false);
    }
  }

  function wireDrawer() {
    var toggle = drawerToggle();
    if (!toggle) { return; }

    toggle.addEventListener("change", function () {
      // When closing in drawer mode, move focus OUT to the hamburger trigger BEFORE the
      // sidebar becomes inert/aria-hidden (so focus is never trapped inside a hidden region).
      if (!toggle.checked && inDrawerMode()) {
        restoreDrawerFocus();
      }
      syncDrawer();
    });

    // Re-evaluate when crossing the drawer breakpoint or on resize (keeps inert/scroll state
    // correct if the viewport changes while the drawer is open).
    if (mq.addEventListener) {
      mq.addEventListener("change", syncDrawer);
    } else if (mq.addListener) {
      mq.addListener(syncDrawer);
    }
    window.addEventListener("resize", syncDrawer);

    // Initial per-page state (on mobile load the closed drawer becomes inert immediately).
    syncDrawer();
  }

  // Escape-to-close and focus trap for the drawer. Capture phase so it runs before the theme,
  // and strictly gated to (drawer mode AND open) so desktop keyboard behaviour is untouched.
  document.addEventListener(
    "keydown",
    function (e) {
      var toggle = drawerToggle();
      if (!toggle || !inDrawerMode() || !toggle.checked) { return; }

      if (e.key === "Escape" || e.key === "Esc") {
        e.preventDefault();
        toggle.checked = false;
        toggle.dispatchEvent(new Event("change", { bubbles: true }));
        return;
      }

      if (e.key === "Tab") {
        var f = drawerFocusables();
        if (!f.length) { return; }
        var first = f[0];
        var last = f[f.length - 1];
        var sb = primarySidebar();
        var active = document.activeElement;

        if (!sb || !sb.contains(active)) {
          // Focus escaped the open drawer → pull it back in.
          e.preventDefault();
          (e.shiftKey ? last : first).focus({ preventScroll: true });
          return;
        }
        if (!e.shiftKey && active === last) {
          e.preventDefault();
          first.focus({ preventScroll: true });
        } else if (e.shiftKey && active === first) {
          e.preventDefault();
          last.focus({ preventScroll: true });
        }
      }
    },
    true
  );

  /* ================================== init (once per page) ================================== */
  function init() {
    if (window.__wvA11yInit) { return; }
    window.__wvA11yInit = true;
    wireSearch();
    wireDrawer();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
