import {
  ApiError,
  androidProfile,
  browserMediaProbe,
  browserProfile,
  playbackMethodKey,
  playbackReasonKeys,
  type ClientProfile,
  type MediaStream,
  type PlaybackMethod,
} from "@atrium/shared";
import { useEvent } from "expo";
import { VideoView, useVideoPlayer, type VideoSource } from "expo-video";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Platform, Pressable, StyleSheet, Text, View } from "react-native";

import { readMediaCapabilities } from "../../modules/atrium-media-capabilities";
import { useActiveServer } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { ErrorMessage } from "../components/ui.tsx";
import { space } from "../theme.ts";
import { absoluteUrl } from "./local";

type Ready = {
  readonly source: VideoSource;
  readonly method: PlaybackMethod;
  readonly reasons: readonly string[];
  readonly streams: readonly MediaStream[];
  readonly audioStream: number | null;
};

/** Qualities by the bit rate the server may send; Auto lets the original play. */
const QUALITIES: readonly (readonly [key: string, bitRate: number | null])[] = [
  ["auto", null],
  ["q1080", 8_400_000],
  ["q720", 4_400_000],
  ["q480", 2_400_000],
  ["q360", 1_400_000],
];

/** Answers that mean no stream would help; the file goes to another app instead. */
const UNPLAYABLE = new Set([
  "playback.unreadable",
  "playback.client_cannot_stream",
  "playback.tone_mapping_unavailable",
]);

const WAIT_MS = 3000;
const WAIT_TRIES = 40;

/**
 * A video in the phone's own player (Media3 through expo-video, D-007,
 * D-067): the original when this phone plays it, otherwise an HLS stream
 * the server makes. What the phone plays comes from Android itself; the
 * server decides. Quality and audio track are chosen here, and what is
 * happening, and why, is one touch away.
 */
export function StreamingPlayer({
  nodeId,
  onUnplayable,
}: {
  readonly nodeId: string;
  readonly onUnplayable: () => void;
}) {
  const { t } = useTranslation();
  const { server, playback } = useActiveServer();
  const [quality, setQuality] = useState(0);
  const [audioStream, setAudioStream] = useState<number | null>(null);
  const [state, setState] = useState<"loading" | "waiting" | Ready | { readonly error: unknown }>("loading");
  const [menu, setMenu] = useState<"quality" | "audio" | null>(null);
  const [info, setInfo] = useState(false);
  const resumeAt = useRef(0);

  const profile = useMemo<ClientProfile>(() => {
    const maxBitRate = QUALITIES[quality]![1];
    const options = maxBitRate === null ? {} : { maxBitRate };
    const android = Platform.OS === "android" ? readMediaCapabilities() : null;
    return android ? androidProfile(android, options) : browserProfile(browserMediaProbe(), options);
  }, [quality]);

  useEffect(() => {
    let cancelled = false;
    let streamId: string | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    setState("loading");

    const run = async (tries: number): Promise<void> => {
      try {
        const decided = await playback.decide(nodeId, profile, audioStream === null ? {} : { audioStream });
        if (cancelled) {
          return;
        }

        if (decided.method === "directPlay" && decided.original) {
          setState({
            source: { uri: absoluteUrl(server.url, decided.original.url) },
            method: decided.method,
            reasons: decided.reasons,
            streams: decided.streams,
            audioStream: decided.audioStream === null ? null : Number(decided.audioStream),
          });
          return;
        }

        const stream = await playback.openStream(nodeId, profile, {
          ...(audioStream === null ? {} : { audioStream }),
          startMs: Math.floor(resumeAt.current * 1000),
        });
        if (cancelled) {
          void playback.closeStream(stream.id).catch(() => undefined);
          return;
        }

        streamId = stream.id;
        setState({
          source: { uri: absoluteUrl(server.url, stream.masterUrl), contentType: "hls" },
          method: stream.method,
          reasons: stream.reasons,
          streams: stream.streams,
          audioStream: stream.audioStream === null ? null : Number(stream.audioStream),
        });
      } catch (error) {
        if (cancelled) {
          return;
        }

        if (error instanceof ApiError && error.problem.code === "playback.not_ready" && tries < WAIT_TRIES) {
          setState("waiting");
          timer = setTimeout(() => void run(tries + 1), WAIT_MS);
          return;
        }

        // Nothing here can play it: offer it to another app, as for any file (D-052).
        if (error instanceof ApiError && UNPLAYABLE.has(error.problem.code)) {
          onUnplayable();
          return;
        }

        setState({ error });
      }
    };

    void run(0);
    return () => {
      cancelled = true;
      clearTimeout(timer);
      if (streamId !== null) {
        void playback.closeStream(streamId).catch(() => undefined);
      }
    };
  }, [nodeId, profile, audioStream, playback, server.url, onUnplayable]);

  const ready = typeof state === "object" && "source" in state ? state : null;
  const player = useVideoPlayer(null, (p) => {
    p.timeUpdateEventInterval = 1;
  });
  const { status } = useEvent(player, "statusChange", { status: player.status });

  useEffect(() => {
    if (!ready) {
      return;
    }

    void player.replaceAsync(ready.source).then(() => {
      if (resumeAt.current > 0) {
        player.currentTime = resumeAt.current;
      }

      player.play();
    });
  }, [ready, player]);

  useEffect(() => {
    // The original did not play after all: offer it to another app.
    if (status === "error" && ready?.method === "directPlay") {
      onUnplayable();
    }
  }, [status, ready, onUnplayable]);

  /** Another track or quality goes on from where it was. */
  const change = (apply: () => void) => {
    resumeAt.current = player.currentTime;
    setMenu(null);
    apply();
  };

  const audio = ready ? ready.streams.filter((s) => s.type === "audio") : [];
  return (
    <View style={[styles.flex, { backgroundColor: "#000" }]}>
      <VideoView player={player} nativeControls contentFit="contain" style={styles.flex} />

      {state === "loading" || state === "waiting" ? (
        <View style={styles.message} pointerEvents="none">
          <Text style={styles.messageText}>{state === "waiting" ? t("player.waiting") : t("common.loading")}</Text>
        </View>
      ) : null}
      {typeof state === "object" && "error" in state ? (
        <View style={styles.message}>
          <ErrorMessage error={state.error} />
        </View>
      ) : null}

      {info && ready ? (
        <View
          style={[styles.info, { backgroundColor: "rgba(0,0,0,0.78)" }]}
          accessibilityRole="summary"
          testID="playback-info"
        >
          <Text style={styles.infoTitle}>{t(playbackMethodKey(ready.method))}</Text>
          {ready.reasons.length > 0 ? (
            ready.reasons.map((reason) => (
              <Text key={reason} style={styles.infoText}>
                {"• "}
                {t([...playbackReasonKeys(reason)])}
              </Text>
            ))
          ) : (
            <Text style={styles.infoText}>{t("player.nothingConverted")}</Text>
          )}
        </View>
      ) : null}

      <View style={styles.tools}>
        {audio.length > 1 ? <Tool label={t("player.audio")} icon="music" onPress={() => setMenu("audio")} /> : null}
        <Tool label={t("player.quality")} icon="settings" onPress={() => setMenu("quality")} />
        <Tool label={t("player.info")} icon="info" selected={info} onPress={() => setInfo((value) => !value)} />
      </View>

      {menu === "quality" ? (
        <Sheet title={t("player.quality")} onClose={() => setMenu(null)}>
          {QUALITIES.map(([key], index) => (
            <SheetOption
              key={key}
              label={t(`player.${key}`)}
              selected={index === quality}
              onPress={() => change(() => setQuality(index))}
            />
          ))}
        </Sheet>
      ) : null}
      {menu === "audio" && ready ? (
        <Sheet title={t("player.audio")} onClose={() => setMenu(null)}>
          {audio.map((track) => (
            <SheetOption
              key={track.index}
              label={[track.language ?? t("player.unknownLanguage"), track.title, (track.codec ?? "").toUpperCase()]
                .filter(Boolean)
                .join(" · ")}
              selected={Number(track.index) === ready.audioStream}
              onPress={() => change(() => setAudioStream(Number(track.index)))}
            />
          ))}
        </Sheet>
      ) : null}
    </View>
  );
}

function Tool(props: {
  readonly label: string;
  readonly icon: "music" | "settings" | "info";
  readonly selected?: boolean;
  readonly onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={props.label}
      accessibilityState={{ selected: props.selected ?? false }}
      onPress={props.onPress}
      hitSlop={8}
      style={[styles.tool, props.selected ? styles.toolOn : null]}
    >
      <Icon name={props.icon} size={22} color="#f3f5f4" />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  message: { position: "absolute", left: space.lg, right: space.lg, top: "45%", alignItems: "center" },
  messageText: { color: "#f3f5f4", fontSize: 16, textAlign: "center" },
  info: {
    position: "absolute",
    top: 64,
    left: space.md,
    right: space.md,
    borderRadius: 10,
    padding: space.md,
    gap: space.xs,
  },
  infoTitle: { color: "#f3f5f4", fontSize: 15, fontWeight: "700" },
  infoText: { color: "#f3f5f4", fontSize: 14, lineHeight: 20 },
  tools: { position: "absolute", top: space.md, right: space.md, flexDirection: "row", gap: space.sm },
  tool: {
    width: 40,
    height: 40,
    borderRadius: 20,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "rgba(0,0,0,0.45)",
  },
  toolOn: { backgroundColor: "rgba(255,255,255,0.3)" },
});
