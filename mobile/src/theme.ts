import { useColorScheme } from "react-native";

/**
 * Atrium's colours, the same as the web app's (docs/UX.md, "Visual
 * language"): warm paper, one patina green for the main action and the
 * current place, gold for what needs care, red for danger. Light and dark
 * follow the system.
 */
export interface Palette {
  readonly paper: string;
  readonly surface: string;
  readonly surfaceSunk: string;
  readonly ink: string;
  readonly inkSoft: string;
  readonly inkFaint: string;
  readonly line: string;
  readonly lineStrong: string;
  readonly accent: string;
  readonly accentStrong: string;
  readonly accentInk: string;
  readonly accentWash: string;
  readonly accentWashInk: string;
  readonly gold: string;
  readonly goldWash: string;
  readonly goldInk: string;
  readonly danger: string;
  readonly dangerWash: string;
  readonly dangerInk: string;
  readonly infoWash: string;
  readonly infoInk: string;
  readonly heroTop: string;
  readonly heroBottom: string;
  readonly heroInk: string;
}

export const light: Palette = {
  paper: "#f6f3ec",
  surface: "#ffffff",
  surfaceSunk: "#faf8f3",
  ink: "#15201c",
  inkSoft: "#45524c",
  inkFaint: "#76827c",
  line: "#e7e2d6",
  lineStrong: "#d4cdbd",
  accent: "#1c6b58",
  accentStrong: "#145244",
  accentInk: "#ffffff",
  accentWash: "#e1efe9",
  accentWashInk: "#134a3c",
  gold: "#b3781c",
  goldWash: "#fbf0d9",
  goldInk: "#74490a",
  danger: "#b3261e",
  dangerWash: "#fceceb",
  dangerInk: "#8a1b15",
  infoWash: "#e9eef6",
  infoInk: "#27456f",
  heroTop: "#17493d",
  heroBottom: "#0b2a23",
  heroInk: "#f2f6f3",
};

export const dark: Palette = {
  paper: "#0d1311",
  surface: "#151c19",
  surfaceSunk: "#111815",
  ink: "#e9efeb",
  inkSoft: "#b7c2bc",
  inkFaint: "#85918b",
  line: "#25302b",
  lineStrong: "#35413b",
  accent: "#62c6a8",
  accentStrong: "#7fd6bb",
  accentInk: "#06211a",
  accentWash: "#14322a",
  accentWashInk: "#a4e6d1",
  gold: "#e0b060",
  goldWash: "#33270f",
  goldInk: "#f3d9a4",
  danger: "#f08b82",
  dangerWash: "#3a1917",
  dangerInk: "#f9c4bf",
  infoWash: "#17263c",
  infoInk: "#bdd2f2",
  heroTop: "#123a31",
  heroBottom: "#07201a",
  heroInk: "#f2f6f3",
};

/** Generous spacing and large rounded corners, in density-independent pixels. */
export const space = { xs: 4, sm: 8, md: 12, lg: 16, xl: 24, xxl: 32 } as const;
export const radius = { sm: 8, md: 14, lg: 20, pill: 999 } as const;

/** Type sizes. The platform's own typeface is used; sizes scale with the system's text size setting. */
export const type = { title: 28, heading: 20, body: 16, small: 14 } as const;

export function useTheme(): { readonly colors: Palette; readonly isDark: boolean } {
  const isDark = useColorScheme() === "dark";
  return { colors: isDark ? dark : light, isDark };
}
