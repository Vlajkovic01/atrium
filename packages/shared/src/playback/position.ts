/**
 * When a video resumes where it was (DESIGN.md 7.4, D-068). The server
 * keeps the last position any device saved; these rules, shared by every
 * player, say whether to start there.
 */

/** Shorter videos always start at the beginning and save nothing: clips, not films. */
export const RESUME_MIN_DURATION_MS = 5 * 60_000;

/** A position this close to the start is the start. */
export const RESUME_MIN_POSITION_MS = 30_000;

/** A position this close to the end, or in the last 5 %, was the end: next time starts over. */
export const RESUME_END_MARGIN_MS = 60_000;

/** How often a playing video saves its place. */
export const SAVE_EVERY_MS = 10_000;

/** Where to start, in milliseconds: the saved position, or 0 when it is too early, too late or the video too short. */
export function resumeFrom(savedMs: number | null | undefined, durationMs: number | null | undefined): number {
  if (savedMs === null || savedMs === undefined || !durationMs || durationMs < RESUME_MIN_DURATION_MS) {
    return 0;
  }

  const endsAt = durationMs - Math.max(RESUME_END_MARGIN_MS, durationMs * 0.05);
  return savedMs < RESUME_MIN_POSITION_MS || savedMs >= endsAt ? 0 : savedMs;
}

/** Whether a video is long enough that its place is saved at all. */
export function savesPosition(durationMs: number | null | undefined): boolean {
  return !!durationMs && durationMs >= RESUME_MIN_DURATION_MS;
}
