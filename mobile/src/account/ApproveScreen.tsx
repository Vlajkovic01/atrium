import { ApiError, normalizePairingCode, parsePairingLink, type PairingDetails } from "@atrium/shared";
import { CameraView, useCameraPermissions } from "expo-camera";
import { useRouter } from "expo-router";
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Pressable, StyleSheet, Text, View } from "react-native";

import { useActiveServer } from "../api.ts";
import { Icon } from "../components/Icon.tsx";
import { Body, Button, ErrorMessage, Notice, Screen, TextField, Title } from "../components/ui.tsx";
import { displayAddress } from "../connect.ts";
import { radius, space, type, useTheme } from "../theme.ts";

type Outcome = "approved" | "denied" | "notFound" | "otherServer" | "notACode";

/**
 * Approving a device without a keyboard, such as a TV (docs/UX.md, Mobile,
 * Approve a device; DESIGN.md 6.5): its QR code is scanned, or its code
 * typed. Before anything is approved, the screen says which device asks,
 * from where, and what it may do; the person approves only a device in
 * front of them.
 */
export function ApproveScreen() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const router = useRouter();
  const { server, devices } = useActiveServer();
  const [permission, requestPermission] = useCameraPermissions();
  const [code, setCode] = useState("");
  const [details, setDetails] = useState<PairingDetails | null>(null);
  const [readOnly, setReadOnly] = useState(false);
  const [hidePersonal, setHidePersonal] = useState(false);
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const scanning = useRef(false);

  const find = async (userCode: string): Promise<void> => {
    setBusy(true);
    setError(null);
    setOutcome(null);
    try {
      setDetails(await devices.pairing(userCode));
    } catch (e) {
      if (e instanceof ApiError && e.problem.status === 404) {
        setOutcome("notFound");
      } else {
        setError(e);
      }
    } finally {
      setBusy(false);
    }
  };

  const onScanned = (text: string): void => {
    if (scanning.current || details) {
      return;
    }

    scanning.current = true;
    const link = parsePairingLink(text);
    if (!link) {
      setOutcome("notACode");
    } else if (!link.server.ok || link.server.url !== server.url) {
      // A code for another server: approving it here would sign the TV in somewhere else.
      setOutcome("otherServer");
    } else {
      void find(link.userCode);
    }

    setTimeout(() => (scanning.current = false), 1500);
  };

  const decide = (approve: boolean): void => {
    if (!details) {
      return;
    }

    setBusy(true);
    setError(null);
    (approve
      ? devices.approve(details.userCode, { readOnly, hidePersonalLibrary: hidePersonal })
      : devices.deny(details.userCode)
    )
      .then(() => {
        setDetails(null);
        setCode("");
        setOutcome(approve ? "approved" : "denied");
      })
      .catch((e: unknown) => setError(e))
      .finally(() => setBusy(false));
  };

  const check = (label: string, on: boolean, toggle: () => void) => (
    <Pressable accessibilityRole="checkbox" accessibilityState={{ checked: on }} onPress={toggle} style={styles.check}>
      <Icon name={on ? "checkCircle" : "circle"} color={on ? colors.accent : colors.inkSoft} />
      <Text style={[styles.checkText, { color: colors.ink }]}>{label}</Text>
    </Pressable>
  );

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
        <Title>{t("devices.approve.heading")}</Title>
      </View>

      {outcome === "approved" ? <Notice kind="accent">{t("devices.approve.approved")}</Notice> : null}
      {outcome === "denied" ? <Notice>{t("devices.approve.denied")}</Notice> : null}
      {outcome === "notFound" ? <Notice kind="warning">{t("devices.approve.notFound")}</Notice> : null}
      {outcome === "notACode" ? <Notice kind="warning">{t("mobile.approve.notACode")}</Notice> : null}
      {outcome === "otherServer" ? (
        <Notice kind="warning">{t("mobile.approve.otherServer", { server: displayAddress(server.url) })}</Notice>
      ) : null}
      <ErrorMessage error={error} />

      {details ? (
        <View style={[styles.card, { backgroundColor: colors.surface, borderColor: colors.line }]}>
          <Text style={[styles.found, { color: colors.ink }]}>
            {t("devices.approve.found", { device: details.deviceName })}
          </Text>
          <Body faint>{[t(`devices.kinds.${details.deviceKind}`), details.platform].filter(Boolean).join(" · ")}</Body>
          {details.requestIp ? <Body faint>{t("devices.approve.from", { address: details.requestIp })}</Body> : null}
          <Text accessibilityLabel={details.userCode} style={[styles.code, { color: colors.ink }]}>
            {details.userCode}
          </Text>
          <Notice kind="warning">{t("devices.approve.warning")}</Notice>
          {check(t("devices.approve.readOnly"), readOnly, () => setReadOnly(!readOnly))}
          {check(t("devices.approve.hidePersonal"), hidePersonal, () => setHidePersonal(!hidePersonal))}
          <Button variant="primary" busy={busy} onPress={() => decide(true)}>
            {t("devices.approve.approve")}
          </Button>
          <Button variant="danger" disabled={busy} onPress={() => decide(false)}>
            {t("devices.approve.deny")}
          </Button>
        </View>
      ) : (
        <>
          <Body>{t("mobile.approve.scanBody")}</Body>
          {permission?.granted ? (
            <View style={[styles.camera, { borderColor: colors.line }]}>
              <CameraView
                style={StyleSheet.absoluteFill}
                facing="back"
                barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
                onBarcodeScanned={({ data }) => onScanned(data)}
              />
            </View>
          ) : (
            <Button onPress={() => void requestPermission()}>{t("mobile.scan.allow")}</Button>
          )}
          <Body faint>{t("devices.approve.body")}</Body>
          <TextField
            label={t("devices.approve.code")}
            value={code}
            onChangeText={setCode}
            autoCapitalize="characters"
            autoCorrect={false}
            placeholder="ABCD-1234"
          />
          <Button
            busy={busy}
            disabled={normalizePairingCode(code) === null}
            onPress={() => {
              const userCode = normalizePairingCode(code);
              if (userCode) {
                void find(userCode);
              }
            }}
          >
            {t("devices.approve.find")}
          </Button>
        </>
      )}
    </Screen>
  );
}

const styles = StyleSheet.create({
  top: { flexDirection: "row", alignItems: "center", gap: space.md },
  card: { borderRadius: radius.md, borderWidth: 1, padding: space.lg, gap: space.md },
  found: { fontSize: type.body, fontWeight: "600" },
  code: {
    fontSize: type.title,
    fontWeight: "700",
    letterSpacing: 4,
    textAlign: "center",
    fontVariant: ["tabular-nums"],
  },
  camera: { height: 260, borderRadius: radius.lg, overflow: "hidden", borderWidth: 1 },
  check: { flexDirection: "row", alignItems: "center", gap: space.sm, minHeight: 44 },
  checkText: { flex: 1, fontSize: type.small },
});
