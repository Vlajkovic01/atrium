import {
  ApiError,
  browserMediaProbe,
  browserProfile,
  playbackApi,
  resumeFrom,
  type ClientProfile,
  type MediaStream,
  type PlaybackMethod,
} from "@atrium/shared";
import { useEffect, useMemo, useState } from "react";

import { api } from "../api.ts";

/** What the player plays: the original, or a stream the server makes, with what the server said about it. */
export type Playback =
  | { readonly state: "loading" }
  | { readonly state: "waiting" }
  | { readonly state: "error"; readonly error: unknown }
  | {
      readonly state: "ready";
      readonly src: string;
      /** An HLS playlist rather than a file. */
      readonly hls: boolean;
      readonly method: PlaybackMethod;
      readonly reasons: readonly string[];
      readonly streams: readonly MediaStream[];
      readonly audioStream: number | null;
      readonly durationMs: number | null;
      /** Where to start: where the person was on any device, or where a change of track or quality left off. */
      readonly startMs: number;
    };

/** How often to ask again while the server is still reading the video, and for how long. */
const WAIT_MS = 3000;
const WAIT_TRIES = 40;

/**
 * Asks the server how this browser plays a video, then opens a stream
 * when the original does not play (DESIGN.md 7.3). The server decides;
 * this only describes the browser. A stream is closed when the player
 * goes or plays something else, so the server stops making it.
 */
export function usePlayback(
  nodeId: string,
  options: { readonly audioStream: number | null; readonly maxBitRate: number | null; readonly startMs: number },
): Playback {
  const { audioStream, maxBitRate, startMs } = options;
  const playback = useMemo(() => playbackApi(api), []);
  const profile = useMemo<ClientProfile>(
    () => browserProfile(browserMediaProbe(), maxBitRate === null ? {} : { maxBitRate }),
    [maxBitRate],
  );
  const [state, setState] = useState<Playback>({ state: "loading" });

  useEffect(() => {
    let cancelled = false;
    let streamId: string | null = null;
    let timer: number | undefined;
    setState({ state: "loading" });

    const run = async (tries: number): Promise<void> => {
      try {
        const decided = await playback.decide(nodeId, profile, audioStream === null ? {} : { audioStream });
        if (cancelled) {
          return;
        }

        const durationMs = decided.durationMs === null ? null : Number(decided.durationMs);
        const start =
          startMs > 0 ? startMs : resumeFrom(decided.resumeMs === null ? null : Number(decided.resumeMs), durationMs);
        if (decided.method === "directPlay" && decided.original) {
          setState({
            state: "ready",
            src: decided.original.url,
            hls: false,
            method: decided.method,
            reasons: decided.reasons,
            streams: decided.streams,
            audioStream: decided.audioStream === null ? null : Number(decided.audioStream),
            durationMs,
            startMs: start,
          });
          return;
        }

        const stream = await playback.openStream(nodeId, profile, {
          ...(audioStream === null ? {} : { audioStream }),
          startMs: start,
        });
        if (cancelled) {
          void playback.closeStream(stream.id).catch(() => undefined);
          return;
        }

        streamId = stream.id;
        setState({
          state: "ready",
          src: stream.masterUrl,
          hls: true,
          method: stream.method,
          reasons: stream.reasons,
          streams: stream.streams,
          audioStream: stream.audioStream === null ? null : Number(stream.audioStream),
          durationMs: stream.durationMs === null ? null : Number(stream.durationMs),
          startMs: start,
        });
      } catch (error) {
        if (cancelled) {
          return;
        }

        if (error instanceof ApiError && error.problem.code === "playback.not_ready" && tries < WAIT_TRIES) {
          setState({ state: "waiting" });
          timer = window.setTimeout(() => void run(tries + 1), WAIT_MS);
          return;
        }

        setState({ state: "error", error });
      }
    };

    void run(0);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
      if (streamId !== null) {
        void playback.closeStream(streamId).catch(() => undefined);
      }
    };
    // startMs is read when a stream opens; changing it alone does not reopen one.
  }, [nodeId, profile, audioStream, playback]);

  return state;
}
