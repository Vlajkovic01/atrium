import { errorMessageKeys, formatBytes, type QueuedUpload } from "@atrium/shared";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, StyleSheet, Text, View } from "react-native";

import { Dialog } from "../components/Dialog.tsx";
import { Icon } from "../components/Icon.tsx";
import { Sheet } from "../components/Sheet.tsx";
import { Body, Button } from "../components/ui.tsx";
import { currentLocale } from "../i18n.ts";
import { radius, space, type, useTheme } from "../theme.ts";
import { useUploads } from "./UploadsProvider.tsx";

const ACTIVE = new Set(["queued", "uploading", "waiting", "paused"]);
/** How long the line stays once all uploads are over. */
const DONE_MS = 10_000;
/** The room the line takes above the tabs, kept free at the bottom of every screen while it shows. */
export const UPLOADS_BAR_ROOM = 72;

function percentOf(item: QueuedUpload): number {
  const status = item.status;
  const offset = "offset" in status ? status.offset : status.state === "completed" ? item.size : 0;
  return item.size === 0 ? 100 : Math.floor((offset / item.size) * 100);
}

/**
 * What is being uploaded, just above the tabs: one line while anything is
 * on its way, and the list of uploads with pause, resume and cancel when
 * touched. Also asks about a taken name when one comes up.
 */
export function UploadsPanel({ bottom }: { readonly bottom: number }) {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const uploads = useUploads();
  const [open, setOpen] = useState(false);
  const locale = currentLocale();
  const active = uploads.items.filter((i) => ACTIVE.has(i.status.state));
  const over = uploads.items.length > 0 && active.length === 0;
  const clear = useRef(uploads.clearFinished);
  clear.current = uploads.clearFinished;

  // Once everything is over, the line goes away by itself after a while, as on the web (D-045);
  // not while the list is open.
  useEffect(() => {
    if (!over || open) {
      return;
    }

    const timer = setTimeout(() => clear.current(), DONE_MS);
    return () => clearTimeout(timer);
  }, [over, open]);
  const total = uploads.items.reduce((n, i) => n + i.size, 0);
  const sent = uploads.items.reduce((n, i) => n + (percentOf(i) / 100) * i.size, 0);
  const overall = total === 0 ? 100 : Math.floor((sent / total) * 100);

  const stateText = (item: QueuedUpload): string => {
    const status = item.status;
    return status.state === "failed"
      ? t([...errorMessageKeys(status.code)], { ...status.params })
      : t(`uploads.state.${status.state}`, { percent: percentOf(item), size: formatBytes(item.size, locale) });
  };

  return (
    <>
      {uploads.items.length > 0 ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("uploads.expand")}
          onPress={() => setOpen(true)}
          style={[styles.bar, { bottom, backgroundColor: colors.surface, borderColor: colors.line }]}
        >
          <Icon name="upload" size={20} color={active.length > 0 ? colors.accent : colors.inkSoft} />
          <View style={styles.barText}>
            <Text numberOfLines={1} style={[styles.barTitle, { color: colors.ink }]}>
              {active.length > 0 ? t("uploads.inProgress", { count: active.length }) : t("uploads.done")}
            </Text>
            <View style={[styles.track, { backgroundColor: colors.line }]}>
              <View style={[styles.fill, { width: `${overall}%`, backgroundColor: colors.accent }]} />
            </View>
          </View>
          <Text style={[styles.percent, { color: colors.inkSoft }]}>{overall}%</Text>
        </Pressable>
      ) : null}

      {open ? (
        <Sheet title={t("uploads.title")} onClose={() => setOpen(false)}>
          {uploads.items.map((item) => (
            <View key={item.id} style={[styles.item, { borderColor: colors.line }]}>
              <View style={styles.itemText}>
                <Text numberOfLines={2} style={[styles.name, { color: colors.ink }]}>
                  {item.name}
                </Text>
                <Text
                  style={[styles.state, { color: item.status.state === "failed" ? colors.danger : colors.inkFaint }]}
                >
                  {stateText(item)}
                </Text>
              </View>
              {item.status.state === "uploading" || item.status.state === "waiting" ? (
                <IconButton icon="pause" label={t("uploads.pause")} onPress={() => uploads.pause(item.id)} />
              ) : null}
              {item.status.state === "paused" ? (
                <IconButton icon="play" label={t("uploads.resume")} onPress={() => uploads.resume(item.id)} />
              ) : null}
              {ACTIVE.has(item.status.state) ? (
                <IconButton icon="close" label={t("uploads.cancel")} onPress={() => uploads.cancel(item.id)} />
              ) : null}
            </View>
          ))}
          {uploads.items.some((i) => !ACTIVE.has(i.status.state)) ? (
            <Button variant="ghost" onPress={uploads.clearFinished}>
              {t("uploads.clear")}
            </Button>
          ) : null}
        </Sheet>
      ) : null}

      {uploads.question ? <ConflictDialog /> : null}
    </>
  );
}

function IconButton({
  icon,
  label,
  onPress,
}: {
  readonly icon: "pause" | "play" | "close";
  readonly label: string;
  readonly onPress: () => void;
}) {
  const { colors } = useTheme();
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress} style={styles.iconButton}>
      <Icon name={icon} size={20} color={colors.inkSoft} />
    </Pressable>
  );
}

/** A name already taken in the folder: keep both, replace (the old file stays as an earlier version) or skip. */
function ConflictDialog() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const { question, answer } = useUploads();
  const [all, setAll] = useState(false);
  if (!question) {
    return null;
  }

  return (
    <Dialog title={t("uploads.conflictTitle")} onClose={() => answer({ choice: "skip", applyToAll: all })}>
      <Body>{t("uploads.conflictBody", { name: question.name })}</Body>
      {question.existingIsFolder ? null : <Body faint>{t("uploads.replaceHelp")}</Body>}
      <Pressable
        accessibilityRole="checkbox"
        accessibilityState={{ checked: all }}
        onPress={() => setAll(!all)}
        style={styles.check}
      >
        <Icon name={all ? "checkCircle" : "circle"} color={all ? colors.accent : colors.inkSoft} />
        <Text style={[styles.checkText, { color: colors.ink }]}>{t("uploads.applyToAll")}</Text>
      </Pressable>
      <View style={styles.answers}>
        <Button variant="primary" onPress={() => answer({ choice: "rename", applyToAll: all })}>
          {t("uploads.keepBoth")}
        </Button>
        {question.existingIsFolder ? null : (
          <Button onPress={() => answer({ choice: "replace", applyToAll: all })}>{t("uploads.replace")}</Button>
        )}
        <Button variant="ghost" onPress={() => answer({ choice: "skip", applyToAll: all })}>
          {t("uploads.skip")}
        </Button>
      </View>
    </Dialog>
  );
}

const styles = StyleSheet.create({
  bar: {
    position: "absolute",
    left: space.md,
    right: space.md,
    minHeight: 56,
    borderRadius: radius.md,
    borderWidth: 1,
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
    elevation: 4,
    shadowColor: "#000",
    shadowOpacity: 0.12,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 2 },
  },
  barText: { flex: 1, gap: space.xs },
  barTitle: { fontSize: type.small, fontWeight: "600" },
  track: { height: 4, borderRadius: 2, overflow: "hidden" },
  fill: { height: 4 },
  percent: { fontSize: type.small, fontVariant: ["tabular-nums"] },
  item: {
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    gap: space.xs,
    paddingHorizontal: space.md,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  itemText: { flex: 1, gap: 2, paddingVertical: space.sm },
  name: { fontSize: type.body, fontWeight: "500" },
  state: { fontSize: type.small },
  iconButton: { width: 44, height: 44, alignItems: "center", justifyContent: "center" },
  check: { flexDirection: "row", alignItems: "center", gap: space.sm, minHeight: 44 },
  checkText: { flex: 1, fontSize: type.small },
  answers: { gap: space.sm },
});
