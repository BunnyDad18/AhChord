/**
 * ST-36 E1: flat-design icon set — single-color inline SVGs (stroke-based,
 * 24×24 grid) that inherit `currentColor`, so they follow the theme and text
 * color. Hand-rolled to match the existing chord-diagram approach; no new
 * dependency. The ♪ brand mark stays as-is.
 */

interface IconProps {
  /** Rendered width/height in px (default 20). */
  size?: number;
  className?: string;
}

function Svg({ size = 20, className, children }: IconProps & { children: React.ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      className={className}
    >
      {children}
    </svg>
  );
}

/** 🐞 replacement — bug report. */
export function BugIcon(props: IconProps) {
  return (
    <Svg {...props}>
      {/* body */}
      <path d="M12 8a4 4 0 0 1 4 4v3a4 4 0 0 1-8 0v-3a4 4 0 0 1 4-4z" />
      {/* antennae */}
      <path d="M9.5 6.5 8 4.5" />
      <path d="m14.5 6.5 1.5-2" />
      {/* legs */}
      <path d="M8 11H5.5" />
      <path d="M8 15l-2.5 1.5" />
      <path d="M16 11h2.5" />
      <path d="m16 15 2.5 1.5" />
    </Svg>
  );
}

/** ☀ replacement — switch to light mode. */
export function SunIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2v2" />
      <path d="M12 20v2" />
      <path d="m4.93 4.93 1.41 1.41" />
      <path d="m17.66 17.66 1.41 1.41" />
      <path d="M2 12h2" />
      <path d="M20 12h2" />
      <path d="m6.34 17.66-1.41 1.41" />
      <path d="m19.07 4.93-1.41 1.41" />
    </Svg>
  );
}

/** ☾ replacement — switch to dark mode. */
export function MoonIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
    </Svg>
  );
}

/** ⚙ replacement — settings (ST-70: account menu). Gear outline + hub; the first
 *  hand-drawn 8-spoke version read as a second sun next to the theme icon at 16px. */
export function GearIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </Svg>
  );
}

/** Sign-out: door with an arrow leaving it (ST-70: account menu). */
export function SignOutIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
      <path d="m16 17 5-5-5-5" />
      <path d="M21 12H9" />
    </Svg>
  );
}

/** ST-70d: bottom-nav tab icons. */
export function MusicNoteIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M9 18V5l12-2v13" />
      <circle cx="6" cy="18" r="3" />
      <circle cx="18" cy="16" r="3" />
    </Svg>
  );
}

export function PlaylistIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M3 6h13" />
      <path d="M3 12h13" />
      <path d="M3 18h8" />
      <path d="m18 15 4 3-4 3z" fill="currentColor" stroke="none" />
    </Svg>
  );
}

export function FolderIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
    </Svg>
  );
}

/** ST-70g: server sync (future MT-04) — two arrows in a circle. */
export function SyncIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M21 12a9 9 0 0 1-15.5 6.2" />
      <path d="M3 12a9 9 0 0 1 15.5-6.2" />
      <path d="M18 2.5v3.3h-3.3" />
      <path d="M6 21.5v-3.3h3.3" />
    </Svg>
  );
}

/** ST-70g: review — an eye (inspect what the scan proposed). */
export function ReviewIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7z" />
      <circle cx="12" cy="12" r="3" />
    </Svg>
  );
}

export function DotsIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <circle cx="5" cy="12" r="1.5" fill="currentColor" stroke="none" />
      <circle cx="12" cy="12" r="1.5" fill="currentColor" stroke="none" />
      <circle cx="19" cy="12" r="1.5" fill="currentColor" stroke="none" />
    </Svg>
  );
}

export function HomeIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M3 10.5 12 3l9 7.5" />
      <path d="M5 9.5V20a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V9.5" />
    </Svg>
  );
}

export function ImportIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M12 3v12" />
      <path d="m7 10 5 5 5-5" />
      <path d="M4 21h16" />
    </Svg>
  );
}

/** 🔀 replacement — random / shuffle. ST-37 E1: curved lines with arrowheads (proper shuffle look). */
export function ShuffleIcon(props: IconProps) {
  return (
    <Svg {...props}>
      {/* Two crossing S-curves, each ending in an arrowhead. */}
      <path d="M2 7h4c6 0 8 10 14 10h2" />
      <polyline points="19 14 22 17 19 20" />
      <path d="M2 17h4c6 0 8-10 14-10h2" />
      <polyline points="19 4 22 7 19 10" />
    </Svg>
  );
}

/** 🔍 replacement — search / scan. */
export function SearchIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <circle cx="11" cy="11" r="8" />
      <line x1="21" y1="21" x2="16.65" y2="16.65" />
    </Svg>
  );
}

/** 🗑 replacement — delete / remove. */
export function TrashIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <polyline points="3 6 5 6 21 6" />
      <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
      <line x1="10" y1="11" x2="10" y2="17" />
      <line x1="14" y1="11" x2="14" y2="17" />
    </Svg>
  );
}

/** 👥 replacement — participants / people. */
export function UsersIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
      <circle cx="9" cy="7" r="4" />
      <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
      <path d="M16 3.13a4 4 0 0 1 0 7.75" />
    </Svg>
  );
}

/** 🔄 replacement — converting / in progress (pair with a spin class). */
export function RefreshIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <polyline points="23 4 23 10 17 10" />
      <polyline points="1 20 1 14 7 14" />
      <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
    </Svg>
  );
}

/** ✓ replacement — success / done. */
export function CheckIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <polyline points="20 6 9 17 4 12" />
    </Svg>
  );
}

/** ✏️ — edit. */
export function EditIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M17 3a2.828 2.828 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" />
    </Svg>
  );
}

/** + — add (e.g. to a playlist). */
export function PlusIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <line x1="12" y1="5" x2="12" y2="19" />
      <line x1="5" y1="12" x2="19" y2="12" />
    </Svg>
  );
}

/** ST-75: ▶ — the primary "play in the player" affordance on list rows. */
export function PlayIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <polygon points="6 4 20 12 6 20 6 4" fill="currentColor" stroke="none" />
    </Svg>
  );
}

/** ST-75: clipboard — copy the ChordPro text (row ⋯ menu). */
export function CopyIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <rect x="9" y="9" width="12" height="12" rx="2" />
      <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
    </Svg>
  );
}

/** ST-75: drag handle (six dots, two columns) on playlist rows. */
export function GripIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <circle cx="9" cy="5" r="1.5" fill="currentColor" stroke="none" />
      <circle cx="15" cy="5" r="1.5" fill="currentColor" stroke="none" />
      <circle cx="9" cy="12" r="1.5" fill="currentColor" stroke="none" />
      <circle cx="15" cy="12" r="1.5" fill="currentColor" stroke="none" />
      <circle cx="9" cy="19" r="1.5" fill="currentColor" stroke="none" />
      <circle cx="15" cy="19" r="1.5" fill="currentColor" stroke="none" />
    </Svg>
  );
}

/** Bug report #87: sidebar collapse chevron (dir 'left' collapses, 'right' expands). */
export function ChevronIcon({ dir, ...props }: IconProps & { dir: 'left' | 'right' }) {
  return (
    <Svg {...props}>
      {dir === 'left' ? <path d="M15 18l-6-6 6-6" /> : <path d="M9 18l6-6-6-6" />}
    </Svg>
  );
}

/** ST-75: capo / key & capo — six strings over a bridge. */
export function GuitarIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M4 5h16" />
      <path d="M4 9h16" />
      <path d="M4 13h16" />
      <path d="M6 17h12" />
      <path d="M8 21h8" />
    </Svg>
  );
}

/** ST-84 polish: close/exit — the phone player's top-right exit (X). */
export function XIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M6 6l12 12" />
      <path d="M18 6L6 18" />
    </Svg>
  );
}

/** Bug report (2026-11-12): full screen — four corners pointing out. */
export function ExpandIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M9 4H4v5" />
      <path d="M15 4h5v5" />
      <path d="M9 20H4v-5" />
      <path d="M15 20h5v-5" />
    </Svg>
  );
}

/** Bug report (2026-11-12): exit full screen — four corners pointing in. */
export function ContractIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M4 9h5V4" />
      <path d="M20 9h-5V4" />
      <path d="M4 15h5v5" />
      <path d="M20 15h-5v5" />
    </Svg>
  );
}
