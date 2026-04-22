/**
 * WebVella ERP Theme TypeScript Interfaces
 *
 * Converts the C# Theme class (WebVella.Erp.Web/Models/Theme.cs) and
 * WebSettings class (WebVella.Erp.Web/Models/WebSettings.cs) into
 * TypeScript interfaces matching the exact JSON wire format.
 *
 * Property names use camelCase corresponding to the snake_case JSON
 * keys produced by the backend's [JsonProperty("...")] attributes.
 */

/**
 * Complete theme configuration for the WebVella ERP application.
 *
 * Contains identity metadata, brand assets, page/header styling,
 * typography settings, semantic Bootstrap-like colors, and the full
 * Material Design color palette (18 hues × 3 variants + extras).
 */
export interface Theme {
  // ───────────────────────────────────────────────────────────────
  // Identity
  // ───────────────────────────────────────────────────────────────

  /** Unique theme identifier (GUID serialised as string). @default "00000000-0000-0000-0000-000000000000" */
  id: string;

  /** Human-readable display label. @default "Default" */
  label: string;

  /** Machine-readable theme name (slug). @default "default" */
  name: string;

  /** Theme description text. @default "this is the default theme of the application" */
  description: string;

  // ───────────────────────────────────────────────────────────────
  // Brand
  // ───────────────────────────────────────────────────────────────

  /** URL or path to the brand logo image. @default "/_content/WebVella.Erp.Web/assets/logo.png" */
  brandLogo: string;

  /** URL or path to the brand logo text image. @default "/_content/WebVella.Erp.Web/assets/logo-text.png" */
  brandLogoText: string;

  /** URL or path to the brand favicon. @default "/_content/WebVella.Erp.Web/assets/favicon.png" */
  brandFavIcon: string;

  /** Brand foreground/text colour. @default "#fff" */
  brandColor: string;

  /** Root URL for the brand link. @default "/" */
  brandUrl: string;

  /** Brand area background colour. @default "#192637" */
  brandBackgroundColor: string;

  /** CSS gradient for the brand inner background. @default "linear-gradient(to bottom,rgba(255,255,255,0.20) 0%, rgba(255,255,255,0.075) 15px,rgba(255,255,255,0) 100%)" */
  brandInnerBackgroundGradient: string;

  /** Brand auxiliary accent colour. @default "#FF9800" */
  brandAuxilaryColor: string;

  /** Brand hover highlight colour. @default "rgba(255,255,255,0.15)" */
  brandHoverColor: string;

  // ───────────────────────────────────────────────────────────────
  // Page & Header
  // ───────────────────────────────────────────────────────────────

  /** Main page background colour. @default "#fff" */
  pageBackgroundColor: string;

  /** Optional CSS background-image for the page. @default "" */
  pageBackgroundImage: string;

  /** Header area background colour. @default "#fff" */
  headerBackgroundColor: string;

  // ───────────────────────────────────────────────────────────────
  // Typography
  // ───────────────────────────────────────────────────────────────

  /** Body text font family name. @default "Roboto" */
  bodyFontFamily: string;

  /** URL to the body font file. @default "/_content/WebVella.Erp.Web/css/Roboto/Roboto-Regular.ttf" */
  bodyFontUrl: string;

  /** Body text font size (CSS value). @default "14px" */
  bodyFontSize: string;

  /** Body text colour. @default "#333" */
  bodyFontColor: string;

  /** Headings font family name. @default "" */
  headingsFontFamily: string;

  /** URL to the headings font file. @default "" */
  headingsFontUrl: string;

  /** Headings text colour. @default "" */
  headingsFontColor: string;

  // ───────────────────────────────────────────────────────────────
  // Border
  // ───────────────────────────────────────────────────────────────

  /** Default gray border colour. @default "#e1e4e8" */
  grayBorderColor: string;

  // ───────────────────────────────────────────────────────────────
  // Semantic Colours (Bootstrap-like)
  // ───────────────────────────────────────────────────────────────

  /** Primary action colour. @default "#007bff" */
  primaryColor: string;

  /** Secondary/muted colour. @default "#6c757d" */
  secondaryColor: string;

  /** Success/positive colour. @default "#28a745" */
  successColor: string;

  /** Danger/destructive colour. @default "#dc3545" */
  dangerColor: string;

  /** Warning/caution colour. @default "#ffc107" */
  warningColor: string;

  /** Informational colour. @default "#17a2b8" */
  infoColor: string;

  /** Light background colour. @default "#f8f9fa" */
  lightColor: string;

  /** Dark foreground colour. @default "#343a40" */
  darkColor: string;

  // ───────────────────────────────────────────────────────────────
  // Material Design Colour Palette
  //
  // Each hue includes base, light, and dark variants.
  // ───────────────────────────────────────────────────────────────

  // — Red ————————————————————————————————————————

  /** Red base colour. @default "#F44336" */
  redColor: string;

  /** Red light variant. @default "#FFEBEE" */
  redLightColor: string;

  /** Red dark variant. @default "#B71C1C" */
  redDarkColor: string;

  // — Pink ———————————————————————————————————————

  /** Pink base colour. @default "#E91E63" */
  pinkColor: string;

  /** Pink light variant. @default "#FCE4EC" */
  pinkLightColor: string;

  /** Pink dark variant. @default "#880E4F" */
  pinkDarkColor: string;

  // — Purple —————————————————————————————————————

  /** Purple base colour. @default "#9C27B0" */
  purpleColor: string;

  /** Purple light variant. @default "#F3E5F5" */
  purpleLightColor: string;

  /** Purple dark variant. @default "#4A148C" */
  purpleDarkColor: string;

  // — Deep Purple ————————————————————————————————

  /** Deep purple base colour. @default "#673AB7" */
  deepPurpleColor: string;

  /** Deep purple light variant. @default "#EDE7F6" */
  deepPurpleLightColor: string;

  /** Deep purple dark variant. @default "#311B92" */
  deepPurpleDarkColor: string;

  // — Indigo —————————————————————————————————————

  /** Indigo base colour. @default "#3F51B5" */
  indigoColor: string;

  /** Indigo light variant. @default "#E8EAF6" */
  indigoLightColor: string;

  /** Indigo dark variant. @default "#1A237E" */
  indigoDarkColor: string;

  // — Blue ———————————————————————————————————————

  /** Blue base colour. @default "#2196F3" */
  blueColor: string;

  /** Blue light variant. @default "#E3F2FD" */
  blueLightColor: string;

  /** Blue dark variant. @default "#0D47A1" */
  blueDarkColor: string;

  // — Light Blue —————————————————————————————————

  /** Light blue base colour. @default "#03A9F4" */
  lightBlueColor: string;

  /** Light blue light variant. @default "#E1F5FE" */
  lightBlueLightColor: string;

  /** Light blue dark variant. @default "#01579B" */
  lightBlueDarkColor: string;

  // — Cyan ———————————————————————————————————————

  /** Cyan base colour. @default "#00BCD4" */
  cyanColor: string;

  /** Cyan light variant. @default "#E0F7FA" */
  cyanLightColor: string;

  /** Cyan dark variant. @default "#006064" */
  cyanDarkColor: string;

  // — Teal ———————————————————————————————————————

  /** Teal base colour. @default "#009688" */
  tealColor: string;

  /** Teal light variant. @default "#E0F2F1" */
  tealLightColor: string;

  /** Teal dark variant. @default "#004D40" */
  tealDarkColor: string;

  // — Green ——————————————————————————————————————

  /** Green base colour. @default "#4CAF50" */
  greenColor: string;

  /** Green light variant. @default "#E8F5E9" */
  greenLightColor: string;

  /** Green dark variant. @default "#1B5E20" */
  greenDarkColor: string;

  // — Light Green ————————————————————————————————

  /** Light green base colour. @default "#8BC34A" */
  lightGreenColor: string;

  /** Light green light variant. @default "#F1F8E9" */
  lightGreenLightColor: string;

  /** Light green dark variant. @default "#33691E" */
  lightGreenDarkColor: string;

  // — Lime ———————————————————————————————————————

  /** Lime base colour. @default "#CDDC39" */
  limeColor: string;

  /** Lime light variant. @default "#F9FBE7" */
  limeLightColor: string;

  /** Lime dark variant. @default "#827717" */
  limeDarkColor: string;

  // — Yellow —————————————————————————————————————

  /** Yellow base colour. @default "#FFEB3B" */
  yellowColor: string;

  /** Yellow light variant. @default "#FFFDE7" */
  yellowLightColor: string;

  /** Yellow dark variant. @default "#F57F17" */
  yellowDarkColor: string;

  // — Amber ——————————————————————————————————————

  /** Amber base colour. @default "#FFC107" */
  amberColor: string;

  /** Amber light variant. @default "#FFF8E1" */
  amberLightColor: string;

  /** Amber dark variant. @default "#FF6F00" */
  amberDarkColor: string;

  // — Orange —————————————————————————————————————

  /** Orange base colour. @default "#FF9800" */
  orangeColor: string;

  /** Orange light variant. @default "#FFF3E0" */
  orangeLightColor: string;

  /** Orange dark variant. @default "#E65100" */
  orangeDarkColor: string;

  // — Deep Orange ————————————————————————————————

  /** Deep orange base colour. @default "#FF5722" */
  deepOrangeColor: string;

  /** Deep orange light variant. @default "#FBE9E7" */
  deepOrangeLightColor: string;

  /** Deep orange dark variant. @default "#BF360C" */
  deepOrangeDarkColor: string;

  // — Brown ——————————————————————————————————————

  /** Brown base colour. @default "#795548" */
  brownColor: string;

  /** Brown light variant. @default "#EFEBE9" */
  brownLightColor: string;

  /** Brown dark variant. @default "#3E2723" */
  brownDarkColor: string;

  // — Gray ———————————————————————————————————————

  /** Gray base colour. @default "#9E9E9E" */
  grayColor: string;

  /** Gray light variant. @default "#FAFAFA" */
  grayLightColor: string;

  /** Gray semi-light variant (between base and light). @default "#ccc" */
  graySemiLightColor: string;

  /** Gray dark variant. @default "#212121" */
  grayDarkColor: string;

  // — Blue Gray ——————————————————————————————————

  /** Blue gray base colour. @default "#607D8B" */
  blueGrayColor: string;

  /** Blue gray light variant. @default "#ECEFF1" */
  blueGrayLightColor: string;

  /** Blue gray dark variant. @default "#263238" */
  blueGrayDarkColor: string;

  // ───────────────────────────────────────────────────────────────
  // Absolute Colours
  // ───────────────────────────────────────────────────────────────

  /** White colour constant. @default "#FFFFFF" */
  whiteColor: string;

  /** Black colour constant. @default "#000000" */
  blackColor: string;
}

/**
 * Application-wide web settings that reference the active theme.
 *
 * Converted from WebVella.Erp.Web/Models/WebSettings.cs.
 */
export interface WebSettings {
  /** GUID of the currently active theme. @default "00000000-0000-0000-0000-000000000000" */
  themeId: string;
}
