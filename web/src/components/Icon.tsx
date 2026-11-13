import type { ReactNode } from "react";

/**
 * Atrium's own icon set: simple line icons on a 24 by 24 grid, drawn here
 * so the app loads nothing from outside and carries no third-party art.
 * Icons are decorative; the text next to them carries the meaning.
 */
const shapes = {
  devices: (
    <>
      <rect x="2.5" y="4.5" width="13" height="9.5" rx="1.8" />
      <path d="M6.5 18h5M9 14v4" />
      <rect x="17" y="8.5" width="4.5" height="11" rx="1.4" />
    </>
  ),
  browser: (
    <>
      <rect x="3" y="4.5" width="18" height="15" rx="2.2" />
      <path d="M3 9h18M6.5 6.8h.01M9 6.8h.01" />
    </>
  ),
  phone: (
    <>
      <rect x="7" y="2.5" width="10" height="19" rx="2.4" />
      <path d="M11 18.2h2" />
    </>
  ),
  tv: (
    <>
      <rect x="2.5" y="5" width="19" height="12" rx="2" />
      <path d="M8 20.5h8" />
    </>
  ),
  device: (
    <>
      <rect x="4" y="4" width="16" height="16" rx="3" />
      <circle cx="12" cy="12" r="2.6" />
    </>
  ),
  user: (
    <>
      <circle cx="12" cy="8.2" r="3.7" />
      <path d="M4.8 20c.9-3.6 3.7-5.6 7.2-5.6s6.300 2 7.200 5.600" />
    </>
  ),
  users: (
    <>
      <circle cx="9" cy="8.5" r="3.3" />
      <path d="M2.8 19.500c.7-3.200 3.100-5 6.200-5s5.500 1.800 6.200 5" />
      <path d="M15.800 5.600a3.300 3.300 0 0 1 0 5.800M17.800 14.800c1.800.6 3 2.200 3.500 4.700" />
    </>
  ),
  drive: (
    <>
      <path d="M4.2 13.200 6.500 5.800A1.800 1.800 0 0 1 8.200 4.500h7.600a1.800 1.800 0 0 1 1.700 1.300l2.300 7.400" />
      <rect x="3" y="13" width="18" height="6.500" rx="2" />
      <path d="M7 16.300h.01M10.200 16.300h.01" />
    </>
  ),
  folder: <path d="M3 7.500A2 2 0 0 1 5 5.500h4.200l2 2.300H19a2 2 0 0 1 2 2v7.700a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />,
  share: (
    <>
      <rect x="3" y="3.500" width="18" height="7" rx="1.800" />
      <path d="M7 7h.01M12 10.500v4M5 18.500h14" />
      <circle cx="12" cy="18.500" r="2" />
    </>
  ),
  cloud: (
    <path d="M7.200 18.500a4.200 4.200 0 0 1-.6-8.360 5.600 5.600 0 0 1 10.900 1.060 3.700 3.700 0 0 1-.5 7.300z" />
  ),
  shield: (
    <>
      <path d="M12 3 5 5.800v5.400c0 4.300 2.900 7.700 7 9.300 4.100-1.600 7-5 7-9.300V5.800z" />
      <path d="m9.200 12 2 2 3.700-4" />
    </>
  ),
  key: (
    <>
      <circle cx="8" cy="14.500" r="4" />
      <path d="m11 11.500 8.500-8.500M16 6.500l2.500 2.500M13.500 9l2 2" />
    </>
  ),
  lock: (
    <>
      <rect x="5" y="10.500" width="14" height="10" rx="2.200" />
      <path d="M8.200 10.500V7.800a3.800 3.800 0 0 1 7.600 0v2.700" />
    </>
  ),
  check: <path d="m5 12.500 4.500 4.500L19 7.500" />,
  checkCircle: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="m8.200 12.300 2.600 2.600 5-5.400" />
    </>
  ),
  alert: (
    <>
      <path d="M12 3.800 2.800 19.500h18.400z" />
      <path d="M12 10v4.200M12 16.800h.01" />
    </>
  ),
  info: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v5.200M12 7.800h.01" />
    </>
  ),
  error: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="m9 9 6 6M15 9l-6 6" />
    </>
  ),
  eye: (
    <>
      <path d="M2.500 12S6 5.500 12 5.500 21.500 12 21.500 12 18 18.500 12 18.500 2.500 12 2.500 12z" />
      <circle cx="12" cy="12" r="2.800" />
    </>
  ),
  eyeOff: (
    <>
      <path d="M4 4l16 16M9.900 5.800A9.200 9.200 0 0 1 12 5.500c6 0 9.500 6.500 9.500 6.500a15.600 15.600 0 0 1-2.700 3.500M6.300 7.600A15.300 15.300 0 0 0 2.500 12S6 18.500 12 18.500a8.800 8.800 0 0 0 3.800-.9" />
      <path d="M10 10.200a2.800 2.800 0 0 0 3.900 3.900" />
    </>
  ),
  logout: <path d="M14 4.500h3.500a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H14M10 8l-4 4 4 4M6 12h9.500" />,
  plus: <path d="M12 5v14M5 12h14" />,
  chevronDown: <path d="m6.500 9.500 5.500 5.500 5.500-5.500" />,
  copy: (
    <>
      <rect x="8.500" y="8.500" width="12" height="12" rx="2" />
      <path d="M5.500 15.500H5a1.500 1.500 0 0 1-1.500-1.500V5A1.500 1.500 0 0 1 5 3.500h9A1.500 1.500 0 0 1 15.500 5v.5" />
    </>
  ),
  refresh: (
    <path d="M20 5v5h-5M4 19v-5h5M19.200 10A7.600 7.600 0 0 0 5.600 8.300M4.800 14a7.600 7.600 0 0 0 13.600 1.700" />
  ),
  globe: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M3 12h18M12 3c2.600 2.500 3.900 5.500 3.900 9s-1.300 6.500-3.900 9c-2.600-2.500-3.900-5.500-3.900-9S9.400 5.500 12 3z" />
    </>
  ),
  qr: (
    <>
      <rect x="4" y="4" width="6" height="6" rx="1" />
      <rect x="14" y="4" width="6" height="6" rx="1" />
      <rect x="4" y="14" width="6" height="6" rx="1" />
      <path d="M14 14h2.500v2.500H14zM20 14v.01M17.500 20H20v-2.500M14 20v.01" />
    </>
  ),
  file: (
    <>
      <path d="M14 3.5H7a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8.5z" />
      <path d="M14 3.5v5h5" />
    </>
  ),
  folderPlus: (
    <>
      <path d="M3 7.5A2 2 0 0 1 5 5.5h4.2l2 2.3H19a2 2 0 0 1 2 2v7.7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
      <path d="M12 11v5M9.5 13.5h5" />
    </>
  ),
  upload: <path d="M12 15.5V4M7.5 8.5 12 4l4.5 4.5M4.5 15v3a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-3" />,
  download: <path d="M12 4v11.5M7.5 11 12 15.5l4.5-4.5M4.5 15v3a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-3" />,
  trash: (
    <>
      <path d="M4.5 7h15M9.5 7V4.8h5V7M6.5 7l.8 12.2a1.8 1.8 0 0 0 1.8 1.6h5.8a1.8 1.8 0 0 0 1.8-1.6L17.5 7" />
      <path d="M10.2 10.5v6.5M13.8 10.5v6.5" />
    </>
  ),
  pencil: <path d="M4 20h4L19 9a2.8 2.8 0 0 0-4-4L4 16zM13.5 6.5l4 4" />,
  move: (
    <path d="M3 7.5A2 2 0 0 1 5 5.5h4.2l2 2.3H19a2 2 0 0 1 2 2v7.7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM9 13.5h6M12.5 11l2.5 2.5-2.5 2.5" />
  ),
  restore: <path d="M4 12a8 8 0 1 0 2.4-5.7M4 4.5v4.3h4.3M12 8v4.3l3 1.8" />,
  history: <path d="M4 12a8 8 0 1 0 2.4-5.7M4 4.5v4.3h4.3M12 8v4.3l3 1.8" />,
  close: <path d="m6 6 12 12M18 6 6 18" />,
  pause: <path d="M8.5 5.5v13M15.5 5.5v13" />,
  play: <path d="m7.5 5 11 7-11 7z" />,
  chevronRight: <path d="m9.5 6 6 6-6 6" />,
  grid: (
    <>
      <rect x="4" y="4" width="6.5" height="6.5" rx="1.5" />
      <rect x="13.5" y="4" width="6.5" height="6.5" rx="1.5" />
      <rect x="4" y="13.5" width="6.5" height="6.5" rx="1.5" />
      <rect x="13.5" y="13.5" width="6.5" height="6.5" rx="1.5" />
    </>
  ),
  list: <path d="M8.5 6.5H20M8.5 12H20M8.5 17.5H20M4.5 6.5h.01M4.5 12h.01M4.5 17.5h.01" />,
  image: (
    <>
      <rect x="3.5" y="4.5" width="17" height="15" rx="2.2" />
      <circle cx="9" cy="10" r="1.7" />
      <path d="m4.5 18 5-5 3.5 3.5 2.5-2.5 4 4" />
    </>
  ),
  album: (
    <>
      <rect x="6.5" y="6.5" width="14" height="13" rx="2" />
      <path d="M3.5 16.5V5.5a2 2 0 0 1 2-2h11" />
      <path d="m8 17.5 3.5-3.5 2.5 2.5 2-2 3 3" />
    </>
  ),
  search: (
    <>
      <circle cx="10.5" cy="10.5" r="6" />
      <path d="m15 15 5.5 5.5" />
    </>
  ),
  chevronLeft: <path d="m14.5 6-6 6 6 6" />,
  chevronUp: <path d="m6 14.5 6-6 6 6" />,
  video: (
    <>
      <rect x="3" y="6" width="13" height="12" rx="2" />
      <path d="m16 10.5 5-3v9l-5-3z" />
    </>
  ),
  bell: <path d="M6 16.500V11a6 6 0 0 1 12 0v5.500l1.500 2h-15zM10 20.500a2.200 2.200 0 0 0 4 0" />,
  star: <path d="m12 3.8 2.5 5.1 5.6.8-4 4 .9 5.6-5-2.7-5 2.7.9-5.6-4-4 5.6-.8z" />,
  heart: (
    <path d="M12 19.5s-7.5-4.400-7.5-10a4.200 4.200 0 0 1 7.500-2.600A4.200 4.200 0 0 1 19.500 9.500c0 5.600-7.500 10-7.500 10z" />
  ),
  pin: (
    <>
      <path d="M12 21s6.5-5.600 6.500-11a6.500 6.500 0 0 0-13 0c0 5.400 6.500 11 6.500 11z" />
      <circle cx="12" cy="10" r="2.400" />
    </>
  ),
  home: <path d="M4 10.500 12 4l8 6.500V19a1.500 1.500 0 0 1-1.500 1.500H15v-6H9v6H5.500A1.500 1.500 0 0 1 4 19z" />,
  briefcase: (
    <>
      <rect x="3.5" y="7.5" width="17" height="12" rx="2" />
      <path d="M9 7.500V5.500a1.500 1.500 0 0 1 1.500-1.500h3A1.500 1.500 0 0 1 15 5.500v2M3.500 13h17" />
    </>
  ),
  tag: (
    <>
      <path d="M3.500 12.200V4.500a1 1 0 0 1 1-1h7.700l8.300 8.300a1.500 1.500 0 0 1 0 2.100l-6.600 6.600a1.500 1.500 0 0 1-2.100 0z" />
      <circle cx="8" cy="8" r="1.300" />
    </>
  ),
  book: (
    <path d="M5 4.500A1.500 1.500 0 0 1 6.500 3H19v15H6.500A1.500 1.500 0 0 0 5 19.500zM5 19.500A1.500 1.500 0 0 0 6.500 21H19v-3" />
  ),
  music: (
    <>
      <path d="M9 17.500V5.500l11-2v12" />
      <circle cx="6.500" cy="17.500" r="2.500" />
      <circle cx="17.500" cy="15.500" r="2.500" />
    </>
  ),
} satisfies Record<string, ReactNode>;

export type IconName = keyof typeof shapes;

/** Icons a person may choose for a menu entry (D-041). */
export const MENU_ICONS: readonly IconName[] = [
  "folder",
  "image",
  "video",
  "album",
  "file",
  "search",
  "star",
  "heart",
  "pin",
  "home",
  "briefcase",
  "tag",
  "book",
  "music",
  "cloud",
  "share",
  "users",
  "lock",
  "trash",
  "devices",
  "user",
];

export function isIconName(name: string | null | undefined): name is IconName {
  return !!name && Object.hasOwn(shapes, name);
}

export function Icon({ name, size = 20 }: { readonly name: IconName; readonly size?: number }) {
  return (
    <svg
      className="icon"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {shapes[name]}
    </svg>
  );
}
