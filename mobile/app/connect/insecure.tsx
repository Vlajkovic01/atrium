import { useLocalSearchParams, useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { Linking } from "react-native";

import { Body, Button, Notice, Screen, Title } from "../../src/components/ui.tsx";

const GUIDE = "https://github.com/Vlajkovic01/atrium/blob/master/deploy/README.md#https";

/**
 * Why a server on plain HTTP outside the local network is refused, and
 * what to set up instead (docs/UX.md, Mobile, Connect; D-049).
 */
export default function Insecure() {
  const { t } = useTranslation();
  const router = useRouter();
  const { server } = useLocalSearchParams<{ server: string }>();

  return (
    <Screen>
      <Title>{t("mobile.insecure.heading")}</Title>
      <Notice kind="warning">{t("mobile.insecure.body", { server })}</Notice>
      <Body>{t("mobile.insecure.local")}</Body>
      <Body>{t("mobile.insecure.fix")}</Body>
      <Button onPress={() => void Linking.openURL(GUIDE)}>{t("mobile.insecure.guide")}</Button>
      {/* Back to the address screen if it is open, with what was typed; a new one after a scan. */}
      <Button variant="primary" onPress={() => router.dismissTo("/connect/address")}>
        {t("mobile.insecure.other")}
      </Button>
    </Screen>
  );
}
