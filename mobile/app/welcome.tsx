import { useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { View } from "react-native";

import { LogoMark } from "../src/components/Logo.tsx";
import { Body, Button, Notice, Screen, Title } from "../src/components/ui.tsx";
import { appVersion } from "../src/device.ts";
import { useServers } from "../src/servers.tsx";
import { space } from "../src/theme.ts";

/** Connecting to a server: the first screen of a new install, and the way to add another server later. */
export default function Welcome() {
  const { t } = useTranslation();
  const router = useRouter();
  const { servers } = useServers();

  return (
    <Screen centered>
      <View style={{ alignItems: "center", gap: space.lg }}>
        <LogoMark size={72} />
        <Title>{t("mobile.welcome.heading")}</Title>
      </View>
      <Body>{t("mobile.welcome.body")}</Body>
      <Button variant="primary" onPress={() => router.push("/connect/scan")}>
        {t("mobile.welcome.scan")}
      </Button>
      <Button onPress={() => router.push("/connect/address")}>{t("mobile.welcome.type")}</Button>
      <Notice kind="accent">{t("mobile.welcome.where")}</Notice>
      {servers.length > 0 ? (
        <Button variant="ghost" onPress={() => router.replace("/account")}>
          {t("common.back")}
        </Button>
      ) : null}
      <Body faint>{t("mobile.about.version", { version: appVersion })}</Body>
    </Screen>
  );
}
