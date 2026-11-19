import { call } from "../api/call.ts";
import type { ApiClient } from "../api/client.ts";
import type { components } from "../api/schema.d.ts";
import type { ClientProfile } from "./profile.ts";

export type PlaybackView = components["schemas"]["PlaybackView"];
export type PlaybackMethod = components["schemas"]["PlaybackMethod"];
export type MediaStream = components["schemas"]["MediaStream"];

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
