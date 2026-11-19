import { appViewKind, fileCategory, formatBytes } from "@atrium/shared";
import { useQuery } from "@tanstack/react-query";
import { useEvent } from "expo";
import { VideoView, useVideoPlayer } from "expo-video";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, Image, Modal, Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import { useActiveServer } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Body, Button, ErrorMessage, Notice } from "../components/ui.tsx";
import { currentLocale } from "../i18n.ts";
import { radius, space, type, useTheme } from "../theme.ts";
import { absoluteUrl, localCopy, openElsewhere, type Viewable } from "./local";
import { copyText, copyUri } from "../offline/copies";
import { useOfflineMaybe } from "../offline/OfflineProvider.tsx";
import { nodeIcon } from "./icons.ts";
import { PdfView } from "./PdfView";
import { StreamingPlayer } from "./StreamingPlayer.tsx";

/** Signed addresses last longer than this; asking again after it keeps them from expiring while shown. */
const ADDRESS_FRESH_MS = 10 * 60 * 1000;

/**
 * Shows a file inside the app (docs/UX.md, Mobile, Files; D-048):
 * pictures, video and sound in the phone's own player, PDF, and text.
 * Anything else, or anything the phone cannot show after all, is offered
 * to another app. The previous and next file of the folder are one touch
 * away; Back closes.
 */
export function FileViewer<T extends Viewable>({
  files,
  index,
  onIndex,
  onClose,
  onDetails,
}: {
  readonly files: readonly T[];
  readonly index: number;
  readonly onIndex: (index: number) => void;
  readonly onClose: () => void;
  readonly onDetails: (node: T) => void;
}) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const node = files[index]!;
  const locale = currentLocale();
  const elsewhere = useOpenElsewhere(node);

  return (
    <Modal animationType="fade" onRequestClose={onClose} statusBarTranslucent>
      <SafeAreaView style={[styles.flex, { backgroundColor: colors.paper }]}>
        <View style={styles.bar}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("common.close")}
            onPress={onClose}
            style={styles.barButton}
          >
            <Icon name="close" />
          </Pressable>
          <View style={styles.titleBox}>
            <Text accessibilityRole="header" numberOfLines={1} style={[styles.title, { color: colors.ink }]}>
              {node.name}
            </Text>
            <Text numberOfLines={1} style={[styles.subtitle, { color: colors.inkFaint }]}>
              {formatBytes(Number(node.size), locale)}
              {files.length > 1 ? ` · ${t("mobile.viewer.position", { index: index + 1, count: files.length })}` : ""}
            </Text>
          </View>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("mobile.viewer.openElsewhere")}
            accessibilityState={{ busy: elsewhere.busy }}
            disabled={elsewhere.busy}
            onPress={elsewhere.open}
            style={styles.barButton}
          >
            {elsewhere.busy ? <ActivityIndicator color={colors.accent} /> : <Icon name="external" />}
          </Pressable>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("files.details")}
            onPress={() => onDetails(node)}
            style={styles.barButton}
          >
            <Icon name="info" />
          </Pressable>
        </View>

        {elsewhere.shown ? <View style={styles.barMessages}>{elsewhere.messages}</View> : null}

        {/* Keyed by the file, so nothing of the previous one (a playing video, an error) carries over. */}
        <ViewerBody key={`${node.id}:${node.version ?? 0}`} node={node} />

        {files.length > 1 ? (
          <View style={[styles.nav, { borderColor: colors.line }]}>
            <Button variant="ghost" disabled={index === 0} onPress={() => onIndex(index - 1)}>
              {t("preview.previous")}
            </Button>
            <Button variant="ghost" disabled={index === files.length - 1} onPress={() => onIndex(index + 1)}>
              {t("preview.next")}
            </Button>
          </View>
        ) : null}
      </SafeAreaView>
    </Modal>
  );
}

function ViewerBody({ node }: { readonly node: Viewable }) {
  const kind = appViewKind(node);
  const [failed, setFailed] = useState(false);
  if (kind === "other" || failed) {
    return <Elsewhere node={node} reason={kind === "other" ? "other" : "cannotShow"} />;
  }

  return kind === "pdf" ? (
    <PdfBody node={node} onError={() => setFailed(true)} />
  ) : (
    <Inline node={node} kind={kind} onError={() => setFailed(true)} />
  );
}

/** What the phone shows from a signed address: pictures, video, sound and text. */
function Inline({
  node,
  kind,
  onError,
}: {
  readonly node: Viewable;
  readonly kind: "image" | "video" | "audio" | "text";
  readonly onError: () => void;
}) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { server, files } = useActiveServer();
  const copy = useOfflineMaybe()?.copyOf(node.id) ?? null;
  const local = copy && copy.version === Number(node.version ?? copy.version) ? copyUri(copy.path!) : null;
  const address = useQuery({
    enabled: local === null,
    queryKey: [server.id, "content-url", node.id, node.version ?? 0],
    queryFn: async () => absoluteUrl(server.url, (await files.contentUrl(node.id, { inline: true })).url),
    staleTime: ADDRESS_FRESH_MS,
  });

  const shown = local ?? address.data;
  if (address.error && !local) {
    return (
      <View style={styles.center}>
        <ErrorMessage error={address.error} />
      </View>
    );
  }

  if (!shown) {
    return <Loading />;
  }

  switch (kind) {
    case "image":
      return (
        <Image
          source={{ uri: shown }}
          accessibilityLabel={node.name}
          resizeMode="contain"
          onError={onError}
          style={[styles.flex, { backgroundColor: colors.surfaceSunk }]}
        />
      );
    case "video":
      // A copy kept on the phone plays as it is; otherwise the server says how (D-067).
      return local ? (
        <Player uri={shown} audio={false} onError={onError} />
      ) : (
        <StreamingPlayer nodeId={node.id} onUnplayable={onError} />
      );
    case "audio":
      return <Player uri={shown} audio onError={onError} />;
    default:
      return <TextBody uri={shown} copy={local ? copy!.path : null} label={t("mobile.viewer.text")} />;
  }
}

function Player({
  uri,
  audio,
  onError,
}: {
  readonly uri: string;
  readonly audio: boolean;
  readonly onError: () => void;
}) {
  const { colors } = useTheme();
  const player = useVideoPlayer(uri, (p) => p.play());
  const { status } = useEvent(player, "statusChange", { status: player.status });
  useEffect(() => {
    // The phone's player cannot play this one: offer it to another app.
    if (status === "error") {
      onError();
    }
  }, [status, onError]);

  return (
    <View style={[styles.flex, { backgroundColor: "#000" }]}>
      {audio ? (
        <View style={styles.audioMark} pointerEvents="none">
          <Icon name="music" size={72} color={colors.inkFaint} />
        </View>
      ) : null}
      <VideoView player={player} nativeControls contentFit="contain" style={styles.flex} />
    </View>
  );
}

function TextBody({
  uri,
  copy,
  label,
}: {
  readonly uri: string;
  readonly copy: string | null;
  readonly label: string;
}) {
  const { colors } = useTheme();
  const text = useQuery({
    queryKey: ["text", uri],
    queryFn: async () => {
      if (copy) {
        return copyText(copy);
      }

      const response = await fetch(uri);
      if (!response.ok) {
        throw new Error(String(response.status));
      }

      return response.text();
    },
    staleTime: Infinity,
  });

  if (text.error) {
    return (
      <View style={styles.center}>
        <ErrorMessage error={text.error} />
      </View>
    );
  }

  return text.data === undefined ? (
    <Loading />
  ) : (
    <ScrollView style={[styles.flex, { backgroundColor: colors.surface }]} contentContainerStyle={styles.textBox}>
      {/* Shown as text, never run or drawn as a page. */}
      <Text selectable accessibilityLabel={label} style={[styles.text, { color: colors.ink }]}>
        {text.data}
      </Text>
    </ScrollView>
  );
}

function PdfBody({ node, onError }: { readonly node: Viewable; readonly onError: () => void }) {
  const { server, files } = useActiveServer();
  const kept = useOfflineMaybe()?.copyOf(node.id) ?? null;
  const copy = useQuery({
    queryKey: [server.id, "local-copy", node.id, node.version ?? 0, kept?.path ?? ""],
    queryFn: () => (kept?.path ? Promise.resolve(copyUri(kept.path)) : localCopy(files, server, node)),
    staleTime: Infinity,
  });

  if (copy.error) {
    return (
      <View style={styles.center}>
        <ErrorMessage error={copy.error} />
      </View>
    );
  }

  return copy.data ? <PdfView uri={copy.data} onError={onError} /> : <Loading />;
}

/** Hands a file to another app, saying when there is none or the file could not be fetched. */
function useOpenElsewhere(node: Viewable) {
  const kept = useOfflineMaybe()?.copyOf(node.id) ?? null;
  const { t } = useTranslation();
  const { server, files } = useActiveServer();
  const [busy, setBusy] = useState(false);
  const [noApp, setNoApp] = useState(false);
  const [error, setError] = useState<unknown>(null);

  // Another file: what was said about the previous one no longer applies.
  useEffect(() => {
    setNoApp(false);
    setError(null);
  }, [node.id]);

  const open = (): void => {
    setBusy(true);
    setError(null);
    setNoApp(false);
    openElsewhere(files, server, node, kept?.path ? copyUri(kept.path) : null)
      .then((result) => setNoApp(result === "noApp"))
      .catch((e: unknown) => setError(e))
      .finally(() => setBusy(false));
  };

  const messages = (
    <>
      {noApp ? <Notice kind="warning">{t("mobile.viewer.noApp")}</Notice> : null}
      <ErrorMessage error={error} />
    </>
  );
  return { open, busy, messages, shown: noApp || error !== null };
}

/** For what Atrium does not show itself, or could not show after all: hand it to another app. */
function Elsewhere({ node, reason }: { readonly node: Viewable; readonly reason: "other" | "cannotShow" }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { open, busy, messages } = useOpenElsewhere(node);

  return (
    <ScrollView contentContainerStyle={styles.center}>
      <View style={[styles.tile, { backgroundColor: colors.surface, borderColor: colors.line }]}>
        <Icon name={nodeIcon({ ...node, kind: "file" })} size={48} color={colors.inkSoft} />
      </View>
      <Text style={[styles.kind, { color: colors.inkSoft }]}>{t(`mobile.files.kinds.${fileCategory(node)}`)}</Text>
      <Body>{t(reason === "other" ? "mobile.viewer.other" : "mobile.viewer.cannotShow")}</Body>
      <Button variant="primary" busy={busy} onPress={open}>
        {t("mobile.viewer.openElsewhere")}
      </Button>
      {messages}
    </ScrollView>
  );
}

function Loading() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  return (
    <View style={styles.center}>
      <ActivityIndicator color={colors.accent} accessibilityLabel={t("common.loading")} />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  bar: { minHeight: 60, flexDirection: "row", alignItems: "center", paddingHorizontal: space.sm, gap: space.xs },
  barButton: { width: 48, height: 48, alignItems: "center", justifyContent: "center" },
  barMessages: { paddingHorizontal: space.lg, paddingBottom: space.sm, gap: space.sm },
  titleBox: { flex: 1, paddingHorizontal: space.xs },
  title: { fontSize: type.body, fontWeight: "600" },
  subtitle: { fontSize: type.small },
  center: { flexGrow: 1, alignItems: "center", justifyContent: "center", gap: space.lg, padding: space.xl },
  tile: {
    width: 96,
    height: 96,
    borderRadius: radius.lg,
    borderWidth: 1,
    alignItems: "center",
    justifyContent: "center",
  },
  kind: { fontSize: type.small, fontWeight: "600" },
  audioMark: { ...StyleSheet.absoluteFill, alignItems: "center", justifyContent: "center" },
  textBox: { padding: space.lg },
  text: { fontFamily: "monospace", fontSize: type.small, lineHeight: 20 },
  nav: { flexDirection: "row", justifyContent: "space-between", borderTopWidth: 1, paddingHorizontal: space.sm },
});
