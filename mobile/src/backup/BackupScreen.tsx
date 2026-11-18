import { errorMessageKeys, formatBytes, formatDateTime } from "@atrium/shared";
import { useQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { Linking, Pressable, StyleSheet, Text, View } from "react-native";

import { Icon } from "../components/Icon.tsx";
import { Body, Button, Heading, Notice, Screen, Title } from "../components/ui.tsx";
import { currentLocale } from "../i18n.ts";
import { askMediaAccess, deviceAlbums } from "../platform/media";
import { radius, space, type, useTheme } from "../theme.ts";
import { useBackup, type BackupActivity, type BackupSettings } from "./BackupProvider.tsx";

/**
 * Camera backup (docs/UX.md, Mobile, Camera backup): off until turned on;
 * what to back up and when; and what it is doing, said as it is: what is
 * on the server, what waits and why, what failed.
 */
export function BackupScreen() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const backup = useBackup();
  const albums = useQuery({ queryKey: ["device-albums"], queryFn: deviceAlbums, enabled: backup.access !== "none" });
  const locale = currentLocale();
  const s = backup.settings;

  const set = (patch: Partial<BackupSettings>): void => void backup.save({ ...s, ...patch });

  const turnOn = async (): Promise<void> => {
    const access = await askMediaAccess();
    if (access === "all" || access === "limited") {
      await backup.save({ ...s, enabled: true });
    }
  };

  const toggle = (label: string, on: boolean, change: () => void) => (
    <Pressable
      accessibilityRole="switch"
      accessibilityState={{ checked: on }}
      aria-checked={on}
      onPress={change}
      style={styles.row}
    >
      <Icon name={on ? "checkCircle" : "circle"} color={on ? colors.accent : colors.inkSoft} />
      <Text style={[styles.label, { color: colors.ink }]}>{label}</Text>
    </Pressable>
  );

  const activityText = (a: BackupActivity): string =>
    a.kind === "checking"
      ? t("mobile.backup.activity.checking", { name: a.name })
      : a.kind === "sending"
        ? t("mobile.backup.activity.sending", { name: a.name, percent: a.percent })
        : t(`mobile.backup.activity.${a.kind}`);

  const card = [styles.card, { backgroundColor: colors.surface, borderColor: colors.line }];
  const counts = backup.counts;

  return (
    <Screen>
      <View style={styles.top}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("common.back")}
          onPress={() => (router.canGoBack() ? router.back() : router.replace("/account"))}
          hitSlop={space.md}
        >
          <Icon name="chevronLeft" />
        </Pressable>
        <Title>{t("mobile.backup.title")}</Title>
      </View>

      {!s.enabled ? (
        <View style={card}>
          <Body>{t("mobile.backup.intro")}</Body>
          <Body faint>{t("mobile.backup.neverDeletes")}</Body>
          {backup.access === "denied" ? (
            <>
              <Notice kind="warning">{t("mobile.backup.denied")}</Notice>
              <Button onPress={() => void Linking.openSettings()}>{t("mobile.scan.openSettings")}</Button>
            </>
          ) : (
            <Button variant="primary" onPress={() => void turnOn()}>
              {t("mobile.backup.turnOn")}
            </Button>
          )}
        </View>
      ) : (
        <>
          {/* What it is doing, said as it is. */}
          <View style={card} accessibilityLiveRegion="polite">
            <Heading>{activityText(backup.activity)}</Heading>
            {counts ? (
              <>
                <Body>{t("mobile.backup.done", { done: counts.done, chosen: counts.chosen })}</Body>
                {counts.waiting > 0 ? (
                  <Body faint>
                    {t("mobile.backup.waiting", {
                      count: counts.waiting,
                      size: formatBytes(counts.bytesWaiting, locale),
                    })}
                  </Body>
                ) : null}
              </>
            ) : null}
            {backup.lastRun ? (
              <Body faint>
                {t("mobile.backup.lastRun", { when: formatDateTime(new Date(backup.lastRun).toISOString(), locale) })}
              </Body>
            ) : null}
            {backup.access === "limited" ? <Notice kind="warning">{t("mobile.backup.limited")}</Notice> : null}
            {backup.failures.length > 0 ? (
              <Notice kind="danger">
                {t("mobile.backup.failed", { count: backup.failures.length })}{" "}
                {backup.failures
                  .slice(0, 3)
                  .map((f) => `${f.name}: ${t([...errorMessageKeys(f.code)])}`)
                  .join(" ")}
              </Notice>
            ) : null}
            <Body faint>{t("mobile.backup.whileOpen")}</Body>
            <Button variant="primary" onPress={backup.runNow}>
              {t("mobile.backup.now")}
            </Button>
          </View>

          <View style={card}>
            <Heading>{t("mobile.backup.what")}</Heading>
            {toggle(t("mobile.backup.photos"), s.photos, () => set({ photos: !s.photos }))}
            {toggle(t("mobile.backup.videos"), s.videos, () => set({ videos: !s.videos }))}
            <Text style={[styles.small, { color: colors.inkSoft }]}>{t("mobile.backup.albums")}</Text>
            <View style={styles.chips}>
              <Chip label={t("mobile.backup.allAlbums")} on={s.albums === null} onPress={() => set({ albums: null })} />
              {(albums.data ?? []).map((album) => {
                const on = s.albums?.includes(album.title) ?? false;
                return (
                  <Chip
                    key={album.id}
                    label={album.title}
                    on={on}
                    onPress={() => {
                      const chosen = new Set(s.albums ?? []);
                      if (on) {
                        chosen.delete(album.title);
                      } else {
                        chosen.add(album.title);
                      }

                      set({ albums: chosen.size === 0 ? null : [...chosen] });
                    }}
                  />
                );
              })}
            </View>
          </View>

          <View style={card}>
            <Heading>{t("mobile.backup.when")}</Heading>
            {toggle(t("mobile.backup.wifiOnly"), s.wifiOnly, () => set({ wifiOnly: !s.wifiOnly }))}
            {toggle(t("mobile.backup.chargingOnly"), s.chargingOnly, () => set({ chargingOnly: !s.chargingOnly }))}
          </View>

          <Body faint>{t("mobile.backup.neverDeletes")}</Body>
          <Button variant="danger" onPress={() => set({ enabled: false })}>
            {t("mobile.backup.turnOff")}
          </Button>
        </>
      )}
    </Screen>
  );
}

function Chip({ label, on, onPress }: { readonly label: string; readonly on: boolean; readonly onPress: () => void }) {
  const { colors } = useTheme();
  return (
    <Pressable
      accessibilityRole="checkbox"
      accessibilityState={{ checked: on }}
      aria-checked={on}
      onPress={onPress}
      style={[
        styles.chip,
        {
          backgroundColor: on ? colors.accentWash : colors.surface,
          borderColor: on ? colors.accentWash : colors.lineStrong,
        },
      ]}
    >
      <Text style={[styles.chipText, { color: on ? colors.accentWashInk : colors.ink }]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  top: { flexDirection: "row", alignItems: "center", gap: space.md },
  card: { borderRadius: radius.md, borderWidth: 1, padding: space.lg, gap: space.md },
  row: { flexDirection: "row", alignItems: "center", gap: space.sm, minHeight: 44 },
  label: { flex: 1, fontSize: type.body },
  small: { fontSize: type.small, fontWeight: "600" },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: space.sm },
  chip: {
    minHeight: 40,
    borderRadius: radius.pill,
    borderWidth: 1,
    paddingHorizontal: space.lg,
    justifyContent: "center",
  },
  chipText: { fontSize: type.small, fontWeight: "600" },
});
