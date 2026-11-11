/**
 * The Atrium mark: an arch opening onto light. An atrium is the open,
 * sunlit court at the centre of a house, which is what the product is
 * meant to be for a household's files.
 */
export function LogoMark({ size = 32 }: { readonly size?: number }) {
  return (
    <svg className="logo-mark" width={size} height={size} viewBox="0 0 32 32" aria-hidden="true" focusable="false">
      <rect className="logo-tile" width="32" height="32" rx="9" />
      <path className="logo-arch" d="M9 24.500V15a7 7 0 0 1 14 0v9.500" />
      <path className="logo-ground" d="M6.500 24.500h19" />
      <circle className="logo-sun" cx="16" cy="15.500" r="2.300" />
    </svg>
  );
}

export function Logo({ size = 32 }: { readonly size?: number }) {
  return (
    <span className="logo">
      <LogoMark size={size} />
      <span className="logo-word">Atrium</span>
    </span>
  );
}

/** The large, quiet drawing behind the sign-in and setup screens: arches receding into light. */
export function HeroArt() {
  return (
    <svg
      className="hero-art"
      viewBox="0 0 600 600"
      preserveAspectRatio="xMidYMin slice"
      aria-hidden="true"
      focusable="false"
    >
      <defs>
        <radialGradient id="hero-light" cx="72%" cy="62%" r="60%">
          <stop offset="0" className="hero-light-core" />
          <stop offset="1" className="hero-light-edge" />
        </radialGradient>
      </defs>
      <rect width="600" height="600" fill="url(#hero-light)" />
      <g className="hero-arches">
        <path d="M150 600V330a220 220 0 0 1 440 0v270" />
        <path d="M205 600V335a165 165 0 0 1 330 0v265" />
        <path d="M258 600V340a112 112 0 0 1 224 0v260" />
        <path d="M308 600V345a62 62 0 0 1 124 0v255" />
      </g>
      <circle className="hero-sun" cx="370" cy="238" r="9" />
    </svg>
  );
}
