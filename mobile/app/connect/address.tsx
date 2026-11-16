import { useRouter } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { Body, Button, ErrorMessage, Screen, TextField, Title } from "../../src/components/ui.tsx";
import { checkAddress } from "../../src/connect.ts";

/** Typing the server's address. The address is checked before anyone is asked for a password. */
export default function Address() {
  const { t } = useTranslation();
  const router = useRouter();
  const [typed, setTyped] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const check = await checkAddress(typed);
      if (check.ok) {
        router.push({ pathname: "/connect/sign-in", params: { server: check.url } });
      } else if (check.reason === "insecure") {
        router.push({ pathname: "/connect/insecure", params: { server: check.typed } });
      } else if (check.reason === "empty" || check.reason === "invalid") {
        setError(t(`mobile.address.${check.reason}`));
      } else {
        setError(t(`mobile.probe.${check.reason}`));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Screen>
      <Title>{t("mobile.address.heading")}</Title>
      <Body>{t("mobile.address.body")}</Body>
      <TextField
        label={t("mobile.address.label")}
        value={typed}
        onChangeText={setTyped}
        placeholder={t("mobile.address.placeholder")}
        autoCapitalize="none"
        autoCorrect={false}
        autoFocus
        inputMode="url"
        returnKeyType="go"
        onSubmitEditing={() => void submit()}
      />
      <ErrorMessage error={error} />
      <Button variant="primary" busy={busy} onPress={() => void submit()}>
        {busy ? t("mobile.address.checking") : t("common.continue")}
      </Button>
      <Button variant="ghost" onPress={() => router.back()}>
        {t("common.back")}
      </Button>
    </Screen>
  );
}
