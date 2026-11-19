import type { ReactNode } from "react";
import Svg, { Circle, Path, Rect } from "react-native-svg";

import { useTheme } from "../theme.ts";

/**
 * Atrium's own line icons on a 24 by 24 grid, the same drawings as the
 * web app's (web/src/components/Icon.tsx), so the app carries no icon
 * font and no third-party art. Icons are decorative; the text beside
 * them, or the button's label, carries the meaning.
 */
const shapes = {
  folder: <Path d="M3 7.5A2 2 0 0 1 5 5.5h4.200l2 2.300H19a2 2 0 0 1 2 2v7.700a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />,
  file: (
    <>
      <Path d="M14 3.5H7a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8.5z" />
      <Path d="M14 3.5v5h5" />
    </>
  ),
  text: (
    <>
      <Path d="M14 3.5H7a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8.5z" />
      <Path d="M14 3.5v5h5M8.500 13h7M8.500 16.500h4.500" />
    </>
  ),
  image: (
    <>
      <Rect x={3.5} y={4.5} width={17} height={15} rx={2.2} />
      <Circle cx={9} cy={10} r={1.7} />
      <Path d="m4.500 18 5-5 3.500 3.500 2.500-2.500 4 4" />
    </>
  ),
  video: (
    <>
      <Rect x={3} y={6} width={13} height={12} rx={2} />
      <Path d="m16 10.500 5-3v9l-5-3z" />
    </>
  ),
  music: (
    <>
      <Path d="M9 17.500V6l10-2v11.500" />
      <Circle cx={6.5} cy={17.5} r={2.5} />
      <Circle cx={16.5} cy={15.5} r={2.5} />
    </>
  ),
  book: (
    <Path d="M5 4.500A1.500 1.500 0 0 1 6.500 3H19v15H6.500A1.500 1.500 0 0 0 5 19.500zM5 19.500A1.500 1.500 0 0 0 6.500 21H19v-3" />
  ),
  archive: (
    <>
      <Rect x={3.5} y={4} width={17} height={5} rx={1.5} />
      <Path d="M5 9v9a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V9M10 13h4" />
    </>
  ),
  user: (
    <>
      <Circle cx={12} cy={8.2} r={3.7} />
      <Path d="M4.800 20c.900-3.600 3.700-5.600 7.200-5.600s6.300 2 7.200 5.600" />
    </>
  ),
  lock: (
    <>
      <Rect x={5} y={10.5} width={14} height={10} rx={2.2} />
      <Path d="M8.200 10.500V7.800a3.800 3.800 0 0 1 7.600 0v2.700" />
    </>
  ),
  info: (
    <>
      <Circle cx={12} cy={12} r={9} />
      <Path d="M12 11v5.200M12 7.800h.01" />
    </>
  ),
  plus: <Path d="M12 5v14M5 12h14" />,
  settings: (
    <>
      <Circle cx={12} cy={12} r={3} />
      <Path d="M12 3.5v2.5M12 18v2.5M3.5 12H6M18 12h2.5M6 6l1.8 1.8M16.2 16.2 18 18M6 18l1.8-1.8M16.2 7.8 18 6" />
    </>
  ),
  folderPlus: (
    <>
      <Path d="M3 7.5A2 2 0 0 1 5 5.5h4.200l2 2.300H19a2 2 0 0 1 2 2v7.700a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
      <Path d="M12 11v5M9.500 13.500h5" />
    </>
  ),
  trash: (
    <>
      <Path d="M4.500 7h15M9.500 7V4.800h5V7M6.500 7l.8 12.200a1.800 1.800 0 0 0 1.800 1.600h5.800a1.800 1.800 0 0 0 1.800-1.600L17.500 7" />
      <Path d="M10.200 10.500v6.500M13.800 10.500v6.500" />
    </>
  ),
  pencil: <Path d="M4 20h4L19 9a2.800 2.800 0 0 0-4-4L4 16zM13.500 6.500l4 4" />,
  move: (
    <Path d="M3 7.500A2 2 0 0 1 5 5.500h4.200l2 2.300H19a2 2 0 0 1 2 2v7.700a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM9 13.500h6M12.500 11l2.500 2.500-2.500 2.500" />
  ),
  copy: (
    <>
      <Rect x={8.5} y={8.5} width={12} height={12} rx={2} />
      <Path d="M5.500 15.500H5a1.500 1.500 0 0 1-1.500-1.500V5A1.500 1.500 0 0 1 5 3.500h9A1.500 1.500 0 0 1 15.500 5v.5" />
    </>
  ),
  restore: <Path d="M4 12a8 8 0 1 0 2.400-5.700M4 4.500v4.300h4.300M12 8v4.300l3 1.800" />,
  camera: (
    <>
      <Path d="M4 8.500A2 2 0 0 1 6 6.500h2l1.500-2h5l1.500 2h2a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z" />
      <Circle cx={12} cy={12.5} r={3.5} />
    </>
  ),
  phone: (
    <>
      <Rect x={7} y={2.5} width={10} height={19} rx={2.4} />
      <Path d="M11 18.200h2" />
    </>
  ),
  tv: (
    <>
      <Rect x={2.5} y={5} width={19} height={12} rx={2} />
      <Path d="M8 20.500h8" />
    </>
  ),
  browser: (
    <>
      <Rect x={3} y={4.5} width={18} height={15} rx={2.2} />
      <Path d="M3 9h18M6.500 6.800h.01M9 6.800h.01" />
    </>
  ),
  bell: <Path d="M6 16.500V11a6 6 0 0 1 12 0v5.500l1.500 2h-15zM10 20.500a2.200 2.200 0 0 0 4 0" />,
  download: <Path d="M12 4v11.500M7.500 11 12 15.500l4.500-4.500M4.500 15v3a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-3" />,
  upload: <Path d="M12 15.500V4M7.500 8.500 12 4l4.500 4.500M4.500 15v3a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-3" />,
  pause: <Path d="M8.500 5.500v13M15.500 5.500v13" />,
  play: <Path d="m7.500 5 11 7-11 7z" />,
  external: <Path d="M14 4h6v6M20 4l-9 9M18 14v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4" />,
  more: <Path d="M12 5.500h.01M12 12h.01M12 18.500h.01" strokeWidth={2.6} />,
  circle: <Circle cx={12} cy={12} r={9} />,
  checkCircle: (
    <>
      <Circle cx={12} cy={12} r={9} />
      <Path d="m8.200 12.300 2.600 2.600 5-5.400" />
    </>
  ),
  grid: (
    <>
      <Rect x={4} y={4} width={6.5} height={6.5} rx={1.5} />
      <Rect x={13.5} y={4} width={6.5} height={6.5} rx={1.5} />
      <Rect x={4} y={13.5} width={6.5} height={6.5} rx={1.5} />
      <Rect x={13.5} y={13.5} width={6.5} height={6.5} rx={1.5} />
    </>
  ),
  search: (
    <>
      <Circle cx={10.5} cy={10.5} r={6} />
      <Path d="m15 15 5.500 5.500" />
    </>
  ),
  album: (
    <>
      <Rect x={6.5} y={6.5} width={14} height={13} rx={2} />
      <Path d="M3.500 16.500V5.500a2 2 0 0 1 2-2h11" />
      <Path d="m8 17.500 3.500-3.500 2.500 2.500 2-2 3 3" />
    </>
  ),
  calendar: (
    <>
      <Rect x={3.5} y={5} width={17} height={15.5} rx={2.2} />
      <Path d="M3.500 10h17M8 3v4M16 3v4" />
    </>
  ),
  sort: <Path d="M7 5v14m0 0-3.500-3.500M7 19l3.500-3.500M17 19V5m0 0-3.500 3.500M17 5l3.500 3.500" />,
  check: <Path d="m5 12.500 4.500 4.500L19 7.500" />,
  close: <Path d="m6 6 12 12M18 6 6 18" />,
  chevronRight: <Path d="m9.500 6 6 6-6 6" />,
  chevronLeft: <Path d="m14.500 6-6 6 6 6" />,
} satisfies Record<string, ReactNode>;

export type IconName = keyof typeof shapes;

export function Icon({
  name,
  size = 24,
  color,
}: {
  readonly name: IconName;
  readonly size?: number;
  /** The ink colour when not given. */
  readonly color?: string;
}) {
  const { colors } = useTheme();
  return (
    <Svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke={color ?? colors.ink}
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      accessible={false}
    >
      {shapes[name]}
    </Svg>
  );
}
