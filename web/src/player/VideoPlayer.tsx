import {
  SAVE_EVERY_MS,
  playbackApi,
  playbackMethodKey,
  playbackReasonKeys,
  savesPosition,
  trickplayFrame,
  type TrickplayView,
} from "@atrium/shared";
import { useQuery } from "@tanstack/react-query";
import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { useTranslation } from "react-i18next";

import { api } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { ErrorMessage } from "../components/ui.tsx";
import { usePlayback } from "./usePlayback.ts";

/** The qualities a person can choose, by the bit rate the server may send; Auto lets the server play the original. */
const QUALITIES: readonly (readonly [key: string, bitRate: number | null])[] = [
  ["auto", null],
  ["q1080", 8_400_000],
  ["q720", 4_400_000],
  ["q480", 2_400_000],
  ["q360", 1_400_000],
];

const SKIP_SECONDS = 10;

/**
 * The web player (docs/UX.md, Player): the original when the browser
 * plays it, a stream the server makes otherwise, with pictures on the seek
 * bar, the audio track and quality, what is happening and why, and the
 * keyboard. hls.js where the browser has Media Source, loaded only then;
 * the browser's own HLS where it has none.
 */
export function VideoPlayer({ nodeId, name }: { readonly nodeId: string; readonly name: string }) {
  const { t } = useTranslation();
  const video = useRef<HTMLVideoElement>(null);
  const frame = useRef<HTMLDivElement>(null);
  const resumeAt = useRef(0);
  const [audioStream, setAudioStream] = useState<number | null>(null);
  const [quality, setQuality] = useState(0);
  const [startMs, setStartMs] = useState(0);
  const playback = usePlayback(nodeId, { audioStream, maxBitRate: QUALITIES[quality]![1], startMs });
  const [playing, setPlaying] = useState(false);
  const [time, setTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [muted, setMuted] = useState(false);
  const [volume, setVolume] = useState(1);
  const [info, setInfo] = useState(false);
  const [hover, setHover] = useState<{ readonly x: number; readonly seconds: number } | null>(null);
  const [failed, setFailed] = useState<string | null>(null);

  const trickplay = useQuery({
    queryKey: ["trickplay", nodeId],
    queryFn: () => playbackApi(api).trickplay(nodeId),
    staleTime: 5 * 60_000,
  });

  const src = playback.state === "ready" ? playback.src : null;
  const hls = playback.state === "ready" && playback.hls;
  const startAt = playback.state === "ready" ? playback.startMs / 1000 : 0;

  // The place is saved while playing, on pause and on closing, for any device to resume (D-068).
  // The last place seen, kept apart from the element, which may be gone when the player closes.
  const place = useRef({ time: 0, duration: 0 });
  const save = useCallback(() => {
    const { time, duration } = place.current;
    if (savesPosition(duration * 1000) && time > 0) {
      void playbackApi(api)
        .savePosition(nodeId, time * 1000, duration * 1000)
        .catch(() => undefined);
    }
  }, [nodeId]);
  useEffect(() => {
    if (!playing) {
      return;
    }

    const timer = window.setInterval(save, SAVE_EVERY_MS);
    return () => window.clearInterval(timer);
  }, [playing, save]);
  useEffect(() => save, [save]);

  // Attach the source; hls.js only where the browser has no HLS of its own.
  useEffect(() => {
    const element = video.current;
    if (!element || src === null) {
      return;
    }

    setFailed(null);
    if (resumeAt.current === 0 && startAt > 0) {
      resumeAt.current = startAt;
    }

    let destroy: (() => void) | undefined;
    let cancelled = false;
    const resume = () => {
      if (resumeAt.current > 0) {
        element.currentTime = resumeAt.current;
        resumeAt.current = 0;
      }

      void element.play().catch(() => undefined);
    };
    element.addEventListener("loadedmetadata", resume, { once: true });

    // hls.js wherever Media Source exists; the browser's own HLS only where
    // it does not (iPhone Safari). Chromium's own HLS is new and refused
    // segments hls.js and Safari play.
    const mediaSource = typeof window.MediaSource !== "undefined" || "ManagedMediaSource" in window;
    if (!hls || (!mediaSource && element.canPlayType("application/vnd.apple.mpegurl") !== "")) {
      element.src = src;
    } else {
      void import("hls.js").then(({ default: Hls }) => {
        if (cancelled) {
          return;
        }

        if (!Hls.isSupported()) {
          setFailed("player.noHls");
          return;
        }

        const player = new Hls({ maxBufferLength: 30 });
        player.on(Hls.Events.ERROR, (_, data) => {
          if (data.fatal) {
            console.warn("HLS playback failed:", data.type, data.details, data.error?.message ?? "");
            setFailed("player.failed");
          }
        });
        player.loadSource(src);
        player.attachMedia(element);
        destroy = () => player.destroy();
      });
    }

    return () => {
      cancelled = true;
      element.removeEventListener("loadedmetadata", resume);
      destroy?.();
      element.removeAttribute("src");
      element.load();
    };
  }, [src, hls, startAt]);

  /** Plays something else of the same video (another track or quality) from where it was. */
  const reopen = useCallback((change: () => void) => {
    const at = video.current?.currentTime ?? 0;
    resumeAt.current = at;
    setStartMs(Math.floor(at * 1000));
    change();
  }, []);

  const toggle = () => {
    const element = video.current;
    if (element) {
      if (element.paused) {
        void element.play().catch(() => undefined);
      } else {
        element.pause();
      }
    }
  };

  const seekTo = (seconds: number) => {
    const element = video.current;
    if (element) {
      element.currentTime = Math.max(0, Math.min(seconds, duration || seconds));
    }
  };

  const fullscreen = () => {
    const box = frame.current;
    if (!box) {
      return;
    }

    if (document.fullscreenElement) {
      void document.exitFullscreen();
    } else {
      void box.requestFullscreen().catch(() => undefined);
    }
  };

  const onKey = (event: KeyboardEvent<HTMLDivElement>) => {
    const element = video.current;
    if (!element || event.target instanceof HTMLSelectElement) {
      return;
    }

    const actions: Record<string, () => void> = {
      " ": toggle,
      k: toggle,
      ArrowLeft: () => seekTo(element.currentTime - SKIP_SECONDS),
      ArrowRight: () => seekTo(element.currentTime + SKIP_SECONDS),
      ArrowUp: () => (element.volume = Math.min(1, element.volume + 0.1)),
      ArrowDown: () => (element.volume = Math.max(0, element.volume - 0.1)),
      m: () => (element.muted = !element.muted),
      f: fullscreen,
      i: () => setInfo((value) => !value),
    };
    const action = actions[event.key];
    if (action) {
      // The viewer around the player uses the arrows to move between files;
      // inside the player they skip instead.
      event.preventDefault();
      event.stopPropagation();
      action();
    }
  };

  const onBar = (event: PointerEvent<HTMLInputElement>) => {
    const box = event.currentTarget.getBoundingClientRect();
    const share = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width));
    setHover({ x: event.clientX - box.left, seconds: share * duration });
  };

  const total =
    duration || (playback.state === "ready" && playback.durationMs !== null ? playback.durationMs / 1000 : 0);
  const audio = playback.state === "ready" ? playback.streams.filter((s) => s.type === "audio") : [];
  const preview = hover && trickplay.data ? trickplayFrame(trickplay.data, hover.seconds * 1000) : null;

  return (
    <div
      className="player"
      ref={frame}
      tabIndex={0}
      onKeyDown={onKey}
      role="region"
      aria-label={t("player.label", { name })}
    >
      <video
        ref={video}
        className="viewer-media player-video"
        playsInline
        onClick={toggle}
        onPlay={() => setPlaying(true)}
        onPause={() => {
          setPlaying(false);
          save();
        }}
        onTimeUpdate={(e) => {
          const element = e.currentTarget;
          setTime(element.currentTime);
          place.current = {
            time: element.currentTime,
            duration: Number.isFinite(element.duration) ? element.duration : 0,
          };
        }}
        onDurationChange={(e) => setDuration(Number.isFinite(e.currentTarget.duration) ? e.currentTarget.duration : 0)}
        onVolumeChange={(e) => {
          setMuted(e.currentTarget.muted);
          setVolume(e.currentTarget.volume);
        }}
        onError={() => setFailed("player.failed")}
        data-testid="player-video"
      />

      {playback.state === "loading" ? <p className="player-message">{t("common.loading")}</p> : null}
      {playback.state === "waiting" ? <p className="player-message">{t("player.waiting")}</p> : null}
      {playback.state === "error" ? (
        <div className="player-message">
          <ErrorMessage error={playback.error} />
        </div>
      ) : null}
      {failed ? <p className="player-message">{t(failed)}</p> : null}

      {info && playback.state === "ready" ? (
        <div className="player-info" role="status" aria-label={t("player.info")}>
          <strong>{t(playbackMethodKey(playback.method))}</strong>
          {playback.reasons.length > 0 ? (
            <ul>
              {playback.reasons.map((reason) => (
                <li key={reason}>{t([...playbackReasonKeys(reason)])}</li>
              ))}
            </ul>
          ) : (
            <p>{t("player.nothingConverted")}</p>
          )}
        </div>
      ) : null}

      <div className="player-controls">
        <div className="player-bar">
          {preview && hover ? <SeekPicture frame={preview} view={trickplay.data!} x={hover.x} /> : null}
          <input
            type="range"
            className="player-seek"
            min={0}
            max={Math.max(1, total)}
            step={1}
            value={Math.min(time, total)}
            aria-label={t("player.seek")}
            aria-valuetext={`${clock(time)} / ${clock(total)}`}
            onChange={(e) => seekTo(Number(e.currentTarget.value))}
            onPointerMove={onBar}
            onPointerLeave={() => setHover(null)}
          />
        </div>
        <div className="player-buttons">
          <button
            type="button"
            className="icon-button"
            onClick={toggle}
            aria-label={playing ? t("player.pause") : t("player.play")}
          >
            <Icon name={playing ? "pause" : "play"} />
          </button>
          <span className="player-time">
            {clock(time)} / {clock(total)}
          </span>
          <button
            type="button"
            className="icon-button"
            aria-label={muted ? t("player.unmute") : t("player.mute")}
            onClick={() => video.current && (video.current.muted = !video.current.muted)}
          >
            <Icon name={muted || volume === 0 ? "mute" : "volume"} />
          </button>
          <input
            type="range"
            className="player-volume"
            min={0}
            max={1}
            step={0.05}
            value={muted ? 0 : volume}
            aria-label={t("player.volume")}
            onChange={(e) => {
              if (video.current) {
                video.current.volume = Number(e.currentTarget.value);
                video.current.muted = false;
              }
            }}
          />
          <span className="player-spacer" />
          {audio.length > 1 ? (
            <select
              aria-label={t("player.audio")}
              value={playback.state === "ready" && playback.audioStream !== null ? playback.audioStream : ""}
              onChange={(e) => {
                const value = Number(e.currentTarget.value);
                reopen(() => setAudioStream(value));
                frame.current?.focus();
              }}
            >
              {audio.map((track) => (
                <option key={track.index} value={Number(track.index)}>
                  {trackName(track, t("player.unknownLanguage"))}
                </option>
              ))}
            </select>
          ) : null}
          <select
            aria-label={t("player.quality")}
            value={quality}
            onChange={(e) => {
              const value = Number(e.currentTarget.value);
              reopen(() => setQuality(value));
              frame.current?.focus();
            }}
          >
            {QUALITIES.map(([key], index) => (
              <option key={key} value={index}>
                {t(`player.${key}`)}
              </option>
            ))}
          </select>
          <button
            type="button"
            className="icon-button"
            aria-pressed={info}
            aria-label={t("player.info")}
            onClick={() => setInfo((value) => !value)}
          >
            <Icon name="info" />
          </button>
          <button type="button" className="icon-button" aria-label={t("player.fullscreen")} onClick={fullscreen}>
            <Icon name="fullscreen" />
          </button>
        </div>
      </div>
    </div>
  );
}

function SeekPicture(props: {
  readonly frame: NonNullable<ReturnType<typeof trickplayFrame>>;
  readonly view: TrickplayView;
  readonly x: number;
}) {
  const { frame, view } = props;
  const columns = Number(view.columns);
  const rows = Number(view.rows);
  return (
    <div
      className="player-picture"
      aria-hidden="true"
      style={{
        left: props.x,
        width: frame.width,
        height: frame.height,
        backgroundImage: `url("${frame.sheet}")`,
        backgroundPosition: `-${frame.x}px -${frame.y}px`,
        backgroundSize: `${columns * frame.width}px ${rows * frame.height}px`,
      }}
    />
  );
}

function trackName(
  track: {
    readonly language?: string | null;
    readonly title?: string | null;
    readonly codec?: string | null;
    readonly channels?: unknown;
  },
  unknownLanguage: string,
): string {
  const language = track.language ? languageName(track.language) : unknownLanguage;
  const channels = track.channels ? ` ${Number(track.channels)}ch` : "";
  return [language, track.title].filter(Boolean).join(" · ") + ` (${(track.codec ?? "").toUpperCase()}${channels})`;
}

/** A three-letter or two-letter language code in the page's language, or the code itself. */
function languageName(code: string): string {
  try {
    return new Intl.DisplayNames([document.documentElement.lang || "en"], { type: "language" }).of(code) ?? code;
  } catch {
    return code;
  }
}

function clock(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = whole % 60;
  const two = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
}
