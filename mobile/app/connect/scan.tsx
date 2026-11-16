import { CameraView, useCameraPermissions } from "expo-camera";
import { useRouter } from "expo-router";
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Linking, StyleSheet, View } from "react-native";

import { Body, Button, ErrorMessage, Notice, Screen, Title } from "../../src/components/ui.tsx";
import { connectWithLink } from "../../src/connect.ts";
import { useServers } from "../../src/servers.tsx";
import { radius, useTheme } from "../../src/theme.ts";

/**
 * Scanning the "connect a phone" code from the web app. The code holds the
 * server's address and a single-use sign-in code, so no password is typed
 * on the phone (DESIGN.md 6.5).
 */
export default function Scan() {
  const { t } = useTranslation();
  const router = useRouter();
  const { colors } = useTheme();
  const { registry } = useServers();
  const [permission, requestPermission] = useCameraPermissions();
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [paused, setPaused] = useState(false);

  // The camera reports the same code many times a second; one is enough.
  const handling = useRef(false);

  const onScanned = async (text: string): Promise<void> => {
    if (handling.current) {
      return;
    }

    handling.current = true;
    setBusy(true);
    setError(null);
    try {
      const outcome = await connectWithLink(registry, text);
      if (outcome.ok) {
        router.dismissTo("/");
        return;
      }

      if (outcome.reason === "insecure") {
        router.replace({ pathname: "/connect/insecure", params: { server: outcome.typed } });
        return;
      }

      setError(outcome.reason === "not_a_code" ? t("mobile.scan.notACode") : t(`mobile.probe.${outcome.reason}`));
      setPaused(true);
    } catch (e) {
      setError(e);
      setPaused(true);
    } finally {
      setBusy(false);
    }
  };

  const scanAgain = (): void => {
    setError(null);
    setPaused(false);
    handling.current = false;
  };

  if (!permission) {
    return <Screen>{null}</Screen>;
  }

  if (!permission.granted) {
    return (
      <Screen>
        <Title>{t("mobile.scan.permissionHeading")}</Title>
        <Body>{t("mobile.scan.permissionBody")}</Body>
        {permission.canAskAgain ? (
          <Button variant="primary" onPress={() => void requestPermission()}>
            {t("mobile.scan.allow")}
          </Button>
        ) : (
          <Button variant="primary" onPress={() => void Linking.openSettings()}>
            {t("mobile.scan.openSettings")}
          </Button>
        )}
        <Button onPress={() => router.replace("/connect/address")}>{t("mobile.welcome.type")}</Button>
        <Button variant="ghost" onPress={() => router.back()}>
          {t("common.back")}
        </Button>
      </Screen>
    );
  }

  return (
    <Screen>
      <Title>{t("mobile.scan.heading")}</Title>
      <Body>{t("mobile.scan.body")}</Body>
      <View style={[styles.frame, { borderColor: colors.lineStrong, backgroundColor: colors.surfaceSunk }]}>
        {paused ? null : (
          <CameraView
            style={StyleSheet.absoluteFill}
            facing="back"
            barcodeScannerSettings={{ barcodeTypes: ["qr"] }}
            onBarcodeScanned={({ data }) => void onScanned(data)}
          />
        )}
      </View>
      {busy ? <Notice kind="accent">{t("mobile.scan.connecting")}</Notice> : null}
      <ErrorMessage error={error} />
      {paused ? (
        <Button variant="primary" onPress={scanAgain}>
          {t("mobile.scan.again")}
        </Button>
      ) : null}
      <Button onPress={() => router.replace("/connect/address")}>{t("mobile.welcome.type")}</Button>
      <Button variant="ghost" onPress={() => router.back()}>
        {t("common.back")}
      </Button>
    </Screen>
  );
}

const styles = StyleSheet.create({
  frame: { aspectRatio: 1, borderRadius: radius.lg, borderWidth: 1, overflow: "hidden" },
});
