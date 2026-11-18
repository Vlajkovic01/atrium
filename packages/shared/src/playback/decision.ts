import { ApiError, call } from "../api/call.ts";
import type { ApiClient } from "../api/client.ts";
import type { components } from "../api/schema.d.ts";
import type { ClientProfile } from "./profile.ts";

export type PlaybackView = components["schemas"]["PlaybackView"];
export type PlaybackMethod = components["schemas"]["PlaybackMethod"];
export type MediaStream = components["schemas"]["MediaStream"];
export type StreamView = components["schemas"]["StreamView"];
export type TrickplayView = components["schemas"]["TrickplayView"];

/** The calls a player makes before it plays (DESIGN.md 7.3). The server decides; clients never guess. */
export function playbackApi(api: ApiClient) {
  return {
    /**
     * How this client plays the video. Rejects with `playback.not_ready`
     * while the server has not read the file yet; a player can wait a
     * moment and ask again.
     */
    decide: (
      id: string,
      profile: ClientProfile,
      options: { readonly audioStream?: number } = {},
    ): Promise<PlaybackView> =>
      call(
        api.POST("/api/v1/nodes/{id}/playback", {
          params: { path: { id } },
          body: { profile, audioStream: options.audioStream ?? null },
        }),
      ),

    /**
     * Opens an HLS stream when the decision was not to play the original.
     * The answer's master URL needs no token: the stream's identifier is
     * its access, and only this session has it.
     */
    openStream: (
      nodeId: string,
      profile: ClientProfile,
      options: { readonly audioStream?: number; readonly startMs?: number } = {},
    ): Promise<StreamView> =>
      call(
        api.POST("/api/v1/playback/sessions", {
          body: { nodeId, profile, audioStream: options.audioStream ?? null, startMs: options.startMs ?? null },
        }),
      ),

    /**
     * Pictures for the seek bar, or null while they are not made yet (or
     * for a video under a minute): the bar then works without them.
     */
    trickplay: async (id: string): Promise<TrickplayView | null> => {
      try {
        return await call(api.GET("/api/v1/nodes/{id}/trickplay", { params: { path: { id } } }));
      } catch (error) {
        if (error instanceof ApiError && error.problem.code === "media.no_trickplay") {
          return null;
        }

        throw error;
      }
    },

    /**
     * Saves where this person is in a video, so any of their devices
     * resumes there (D-068). Players save every few seconds while playing,
     * on pause and when closed.
     */
    savePosition: (nodeId: string, positionMs: number, durationMs: number): Promise<void> =>
      call(
        api.PUT("/api/v1/playback/state/{nodeId}", {
          params: { path: { nodeId } },
          body: { positionMs: Math.round(positionMs), durationMs: Math.round(durationMs) },
        }),
      ),

    /** Ends a stream, so the server stops making it. */
    closeStream: (id: string): Promise<void> =>
      call(api.DELETE("/api/v1/playback/sessions/{id}", { params: { path: { id } } })),
  };
}

/** The translation key of a way of playing, for the playback info panel: "playback.method.remux". */
export function playbackMethodKey(method: PlaybackMethod): string {
  return `playback.method.${method}`;
}

/**
 * The translation keys of a reason the server gave, with a general one
 * after it, so a reason added by a newer server still reads as something.
 */
export function playbackReasonKeys(reason: string): readonly string[] {
  return [`playback.reason.${reason}`, "playback.reason.other"];
}

/** Where in a sheet the picture of a position is, in the sheet's pixels. */
export interface TrickplayFrame {
  readonly sheet: string;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
}

/**
 * The seek picture for a position: the picture of the interval it falls
 * in, the last one past the end. Pictures run left to right, then top to
 * bottom, sheet by sheet.
 */
export function trickplayFrame(view: TrickplayView, positionMs: number): TrickplayFrame | null {
  const count = Number(view.count);
  const columns = Number(view.columns);
  const rows = Number(view.rows);
  const width = Number(view.width);
  const height = Number(view.height);
  if (count <= 0 || columns <= 0 || rows <= 0) {
    return null;
  }

  const index = Math.min(count - 1, Math.max(0, Math.floor(positionMs / Number(view.intervalMs))));
  const perSheet = columns * rows;
  const sheet = view.sheets[Math.floor(index / perSheet)];
  if (sheet === undefined) {
    return null;
  }

  const within = index % perSheet;
  return { sheet, x: (within % columns) * width, y: Math.floor(within / columns) * height, width, height };
}
