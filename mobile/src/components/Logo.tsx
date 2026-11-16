import Svg, { Circle, Path, Rect } from "react-native-svg";

import { useTheme } from "../theme.ts";

/** The Atrium mark, as in the web app: an arch opening onto a small sun. */
export function LogoMark({ size = 32 }: { readonly size?: number }) {
  const { colors } = useTheme();
  return (
    <Svg width={size} height={size} viewBox="0 0 32 32" accessible={false}>
      <Rect width={32} height={32} rx={9} fill={colors.accent} />
      <Path
        d="M9 24.5V15a7 7 0 0 1 14 0v9.5"
        fill="none"
        stroke={colors.accentInk}
        strokeWidth={2.2}
        strokeLinecap="round"
      />
      <Path d="M6.5 24.5h19" fill="none" stroke={colors.accentInk} strokeWidth={2.2} strokeLinecap="round" />
      <Circle cx={16} cy={15.5} r={2.3} fill={colors.gold} />
    </Svg>
  );
}
