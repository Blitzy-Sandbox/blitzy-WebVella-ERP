/**
 * Tailwind CSS 4.x Configuration for WebVella ERP React SPA
 *
 * Replaces the Bootstrap 4 theme from the monolith. All design tokens are
 * extracted from the original `Theme.cs` model (80+ color/font/brand properties)
 * and the tokenized `styles.css` (CSS custom properties). This configuration
 * provides a comprehensive design system mapping every monolith token to
 * Tailwind utility classes.
 *
 * Key mappings:
 * - Brand colors  → theme.extend.colors.brand.*
 * - Bootstrap semantic colors → theme.extend.colors.{primary,secondary,success,danger,warning,info}
 * - Material Design palette (19 families) → theme.extend.colors.mat-*
 * - Sidebar chrome → theme.extend.colors.sidebar.*
 * - Typography → theme.extend.fontFamily.sans (Roboto)
 * - 12-column grid (PcRow replacement) → theme.extend.gridTemplateColumns['12']
 * - Border default → theme.extend.borderColor.DEFAULT (#e1e4e8)
 *
 * No Bootstrap imports — Bootstrap 4 is fully replaced by Tailwind utilities.
 */
import type { Config } from 'tailwindcss';

/**
 * Complete Tailwind CSS configuration object that maps every design token from
 * the WebVella ERP monolith Theme.cs model to Tailwind CSS 4 custom theme values.
 *
 * Content paths include the SPA source, the index.html entry point, and the
 * shared-ui library so that Tailwind can tree-shake unused utility classes from
 * all consumed component sources.
 */
const tailwindConfig = {
  /* ------------------------------------------------------------------ */
  /*  Content — files Tailwind scans for class usage (tree-shaking)     */
  /* ------------------------------------------------------------------ */
  content: [
    './index.html',
    './src/**/*.{js,ts,jsx,tsx}',
    '../../libs/shared-ui/src/**/*.{js,ts,jsx,tsx}',
  ],

  /* ------------------------------------------------------------------ */
  /*  Theme — extends the default Tailwind theme with ERP tokens        */
  /* ------------------------------------------------------------------ */
  theme: {
    extend: {
      /* ============================================================== */
      /*  Colors                                                        */
      /*  Source: WebVella.Erp.Web/Models/Theme.cs default property vals */
      /* ============================================================== */
      colors: {
        /* -------------------------------------------------------------- */
        /*  Brand colors                                                  */
        /*  Theme.cs: BrandBackgroundColor, BrandColor, BrandAuxilaryColor*/
        /* -------------------------------------------------------------- */
        brand: {
          DEFAULT: '#192637',
          text: '#ffffff',
          accent: '#FF9800',
          hover: 'rgba(255, 255, 255, 0.15)',
          gradient:
            'linear-gradient(to bottom, rgba(255,255,255,0.20) 0%, rgba(255,255,255,0.075) 15px, rgba(255,255,255,0) 100%)',
        },

        /* -------------------------------------------------------------- */
        /*  Semantic colors (Bootstrap 4 equivalents with shade scales)   */
        /*  Shade 500 equals the monolith DEFAULT value                   */
        /* -------------------------------------------------------------- */
        primary: {
          50: '#e6f0ff',
          100: '#cce0ff',
          200: '#99c2ff',
          300: '#66a3ff',
          400: '#3385ff',
          DEFAULT: '#007bff',
          500: '#007bff',
          600: '#0062cc',
          700: '#004a99',
          800: '#003166',
          900: '#001933',
        },
        secondary: {
          50: '#f0f1f2',
          100: '#d6d8db',
          200: '#bcc0c4',
          300: '#a2a8ae',
          400: '#878f97',
          DEFAULT: '#6c757d',
          500: '#6c757d',
          600: '#565e64',
          700: '#41464b',
          800: '#2b2f32',
          900: '#161719',
        },
        success: {
          50: '#e8f5e9',
          100: '#c3e6cb',
          200: '#9dd5a5',
          300: '#71c47f',
          400: '#4cb862',
          DEFAULT: '#28a745',
          500: '#28a745',
          600: '#218838',
          700: '#19692b',
          800: '#114a1e',
          900: '#092b11',
        },
        danger: {
          50: '#fce4e6',
          100: '#f5c6cb',
          200: '#eda2a9',
          300: '#e57f88',
          400: '#de5c68',
          DEFAULT: '#dc3545',
          500: '#dc3545',
          600: '#c82333',
          700: '#a71d2a',
          800: '#851721',
          900: '#641018',
        },
        warning: {
          50: '#fff8e1',
          100: '#ffecb3',
          200: '#ffe082',
          300: '#ffd54f',
          400: '#ffca28',
          DEFAULT: '#ffc107',
          500: '#ffc107',
          600: '#e0a800',
          700: '#c69500',
          800: '#a07800',
          900: '#7a5c00',
        },
        info: {
          50: '#e3f5f8',
          100: '#b8e5ed',
          200: '#8ed4e2',
          300: '#63c3d7',
          400: '#3db3ca',
          DEFAULT: '#17a2b8',
          500: '#17a2b8',
          600: '#138496',
          700: '#0f6674',
          800: '#0a4852',
          900: '#062a30',
        },

        /* -------------------------------------------------------------- */
        /*  Light / Dark utility colors (Theme.cs LightColor / DarkColor) */
        /* -------------------------------------------------------------- */
        light: {
          DEFAULT: '#f8f9fa',
          50: '#ffffff',
          100: '#fdfdfe',
          200: '#f8f9fa',
          300: '#e9ecef',
          400: '#dee2e6',
        },
        dark: {
          DEFAULT: '#343a40',
          400: '#495057',
          500: '#343a40',
          600: '#2b3035',
          700: '#212529',
          800: '#1a1e21',
          900: '#111315',
        },

        /* -------------------------------------------------------------- */
        /*  Sidebar chrome (Theme.cs SidebarBackgroundColor / SidebarColor)*/
        /* -------------------------------------------------------------- */
        sidebar: {
          DEFAULT: '#1B2A3B',
          text: '#ffffff',
          hover: 'rgba(255, 255, 255, 0.08)',
          active: 'rgba(255, 255, 255, 0.12)',
        },

        /* -------------------------------------------------------------- */
        /*  Page / Layout tokens from Theme.cs                            */
        /* -------------------------------------------------------------- */
        page: {
          DEFAULT: '#ffffff',
          bg: '#ffffff',
          header: '#ffffff',
        },
        body: {
          DEFAULT: '#333333',
          text: '#333333',
          muted: '#aaaaaa',
        },

        /* -------------------------------------------------------------- */
        /*  Link colors (from styles.css anchor defaults)                 */
        /* -------------------------------------------------------------- */
        link: {
          DEFAULT: '#2196F3',
          hover: '#0D47A1',
        },

        /* -------------------------------------------------------------- */
        /*  Material Design Palette — 19 color families                   */
        /*  Each with DEFAULT (base), light, and dark variants            */
        /*  Source: Theme.cs properties (e.g. RedColor, RedLightColor, …) */
        /*  Prefixed with "mat-" to avoid conflicts with Tailwind defaults*/
        /* -------------------------------------------------------------- */

        /* Red — #F44336 */
        'mat-red': {
          DEFAULT: '#F44336',
          light: '#FFEBEE',
          dark: '#B71C1C',
        },

        /* Pink — #E91E63 */
        'mat-pink': {
          DEFAULT: '#E91E63',
          light: '#FCE4EC',
          dark: '#880E4F',
        },

        /* Purple — #9C27B0 */
        'mat-purple': {
          DEFAULT: '#9C27B0',
          light: '#F3E5F5',
          dark: '#4A148C',
        },

        /* Deep Purple — #673AB7 */
        'mat-deep-purple': {
          DEFAULT: '#673AB7',
          light: '#EDE7F6',
          dark: '#311B92',
        },

        /* Indigo — #3F51B5 */
        'mat-indigo': {
          DEFAULT: '#3F51B5',
          light: '#E8EAF6',
          dark: '#1A237E',
        },

        /* Blue — #2196F3 */
        'mat-blue': {
          DEFAULT: '#2196F3',
          light: '#E3F2FD',
          dark: '#0D47A1',
        },

        /* Light Blue — #03A9F4 */
        'mat-light-blue': {
          DEFAULT: '#03A9F4',
          light: '#E1F5FE',
          dark: '#01579B',
        },

        /* Cyan — #00BCD4 */
        'mat-cyan': {
          DEFAULT: '#00BCD4',
          light: '#E0F7FA',
          dark: '#006064',
        },

        /* Teal — #009688 */
        'mat-teal': {
          DEFAULT: '#009688',
          light: '#E0F2F1',
          dark: '#004D40',
        },

        /* Green — #4CAF50 */
        'mat-green': {
          DEFAULT: '#4CAF50',
          light: '#E8F5E9',
          dark: '#1B5E20',
        },

        /* Light Green — #8BC34A */
        'mat-light-green': {
          DEFAULT: '#8BC34A',
          light: '#F1F8E9',
          dark: '#33691E',
        },

        /* Lime — #CDDC39 */
        'mat-lime': {
          DEFAULT: '#CDDC39',
          light: '#F9FBE7',
          dark: '#827717',
        },

        /* Yellow — #FFEB3B */
        'mat-yellow': {
          DEFAULT: '#FFEB3B',
          light: '#FFFDE7',
          dark: '#F57F17',
        },

        /* Amber — #FFC107 */
        'mat-amber': {
          DEFAULT: '#FFC107',
          light: '#FFF8E1',
          dark: '#FF6F00',
        },

        /* Orange — #FF9800 */
        'mat-orange': {
          DEFAULT: '#FF9800',
          light: '#FFF3E0',
          dark: '#E65100',
        },

        /* Deep Orange — #FF5722 */
        'mat-deep-orange': {
          DEFAULT: '#FF5722',
          light: '#FBE9E7',
          dark: '#BF360C',
        },

        /* Brown — #795548 */
        'mat-brown': {
          DEFAULT: '#795548',
          light: '#EFEBE9',
          dark: '#3E2723',
        },

        /* Gray (Material) — #9E9E9E */
        'mat-gray': {
          DEFAULT: '#9E9E9E',
          light: '#FAFAFA',
          'semi-light': '#cccccc',
          dark: '#212121',
        },

        /* Blue Gray — #607D8B */
        'mat-blue-gray': {
          DEFAULT: '#607D8B',
          light: '#ECEFF1',
          dark: '#263238',
        },
      },

      /* ============================================================== */
      /*  Font Family                                                   */
      /*  Source: Theme.cs BodyFontFamily = "Roboto"                    */
      /*  Replaces Bootstrap 4 default system stack with Roboto-first   */
      /* ============================================================== */
      fontFamily: {
        sans: [
          'Roboto',
          '-apple-system',
          'BlinkMacSystemFont',
          '"Segoe UI"',
          '"Helvetica Neue"',
          'Arial',
          'sans-serif',
          '"Apple Color Emoji"',
          '"Segoe UI Emoji"',
          '"Segoe UI Symbol"',
        ],
      },

      /* ============================================================== */
      /*  Font Size                                                     */
      /*  Source: Theme.cs BodyFontSize = "14px"                        */
      /*  Overrides Tailwind's default base (16px / 1rem) to match the  */
      /*  monolith's slightly smaller base type size                    */
      /* ============================================================== */
      fontSize: {
        base: ['14px', { lineHeight: '1.5' }],
        xs: ['0.75rem', { lineHeight: '1.25' }],
        sm: ['0.8125rem', { lineHeight: '1.25' }],
        lg: ['1.25rem', { lineHeight: '1.25' }],
        xl: ['1.75rem', { lineHeight: '1.25' }],
      },

      /* ============================================================== */
      /*  Border Color                                                  */
      /*  Source: Theme.cs GrayBorderColor = "#e1e4e8"                  */
      /*  Sets the default border color used by `border` utility        */
      /* ============================================================== */
      borderColor: {
        DEFAULT: '#e1e4e8',
      },

      /* ============================================================== */
      /*  Grid Template Columns                                         */
      /*  Replaces Bootstrap 4 12-column grid system (PcRow component)  */
      /*  Usage: className="grid grid-cols-12"                          */
      /* ============================================================== */
      gridTemplateColumns: {
        '12': 'repeat(12, minmax(0, 1fr))',
      },

      /* ============================================================== */
      /*  Spacing overrides to match Bootstrap 4 spacing scale          */
      /*  BS4: 0=0, 1=0.25rem, 2=0.5rem, 3=1rem, 4=1.5rem, 5=3rem    */
      /* ============================================================== */
      spacing: {
        '4.5': '1.125rem',
        '13': '3.25rem',
        '15': '3.75rem',
        '18': '4.5rem',
        '22': '5.5rem',
        sidebar: '250px',
        'sidebar-collapsed': '60px',
      },

      /* ============================================================== */
      /*  Box Shadow — matching Bootstrap 4 card/dropdown shadows       */
      /* ============================================================== */
      boxShadow: {
        card: '0 1px 3px rgba(0, 0, 0, 0.12), 0 1px 2px rgba(0, 0, 0, 0.08)',
        dropdown: '0 2px 8px rgba(0, 0, 0, 0.15)',
        modal: '0 5px 15px rgba(0, 0, 0, 0.5)',
      },

      /* ============================================================== */
      /*  Border Radius — matching Bootstrap 4 defaults                 */
      /* ============================================================== */
      borderRadius: {
        sm: '0.2rem',
        DEFAULT: '0.25rem',
        lg: '0.3rem',
      },

      /* ============================================================== */
      /*  Z-Index — matching Bootstrap 4 z-index scale                  */
      /* ============================================================== */
      zIndex: {
        dropdown: '1000',
        sticky: '1020',
        fixed: '1030',
        'modal-backdrop': '1040',
        modal: '1050',
        popover: '1060',
        tooltip: '1070',
      },

      /* ============================================================== */
      /*  Min / Max Width helpers for layout containers                 */
      /* ============================================================== */
      minWidth: {
        sidebar: '250px',
        'sidebar-collapsed': '60px',
      },
      maxWidth: {
        'form-control': '100%',
      },

      /* ============================================================== */
      /*  Transition — smooth transitions for sidebar and UI elements   */
      /* ============================================================== */
      transitionDuration: {
        DEFAULT: '200ms',
      },
    },
  },

  /* ------------------------------------------------------------------ */
  /*  Plugins — no external Tailwind plugins required at this time      */
  /* ------------------------------------------------------------------ */
  plugins: [],
} satisfies Config;

export default tailwindConfig;
