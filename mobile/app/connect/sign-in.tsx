import { isUnencrypted, signInTo } from "@atrium/shared";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { Body, Button, ErrorMessage, Notice, Screen, TextField, Title } from "../../src/components/ui.tsx";
import { displayAddress } from "../../src/connect.ts";
import { thisDevice } from "../../src/device.ts";
import { useServers } from "../../src/servers.tsx";

/** Name and password, then the second step when the account has one. The same two steps as on the web. */
export default function SignIn() {
  const { t } = useTranslation();
  const router = useRouter();
  const { registry } = useServers();
  const { server } = useLocalSearchParams<{ server: string }>();
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [challenge, setChallenge] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<void>): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const submitPassword = (): Promise<void> =>
    run(async () => {
      const result = await signInTo(server, thisDevice()).withPassword(name.trim(), password);
      if (result.status === "totpRequired") {
        setChallenge(result.challenge);
        setPassword("");
        return;
      }

      await registry.add(server, result.tokens);
      router.dismissTo("/");
    });

  const submitCode = (): Promise<void> =>
    run(async () => {
      const tokens = await signInTo(server, thisDevice()).withTotp(challenge ?? "", code.trim());
      await registry.add(server, tokens);
      router.dismissTo("/");
    });

  return (
    <Screen>
      <Title>{challenge === null ? t("signIn.heading") : t("signIn.totpHeading")}</Title>
      <Body>{t("mobile.signIn.server", { server: displayAddress(server) })}</Body>
      {isUnencrypted(server) ? <Notice kind="warning">{t("connection.insecure")}</Notice> : null}
      {challenge === null ? (
        <>
          <TextField
            label={t("signIn.name")}
            value={name}
            onChangeText={setName}
            autoCapitalize="none"
            autoCorrect={false}
            autoComplete="username"
            autoFocus
            returnKeyType="next"
          />
          <TextField
            label={t("signIn.password")}
            value={password}
            onChangeText={setPassword}
            secureTextEntry
            autoComplete="current-password"
            returnKeyType="go"
            onSubmitEditing={() => void submitPassword()}
          />
          <ErrorMessage error={error} />
          <Button variant="primary" busy={busy} disabled={!name || !password} onPress={() => void submitPassword()}>
            {t("signIn.submit")}
          </Button>
          <Button variant="ghost" onPress={() => router.back()}>
            {t("common.back")}
          </Button>
        </>
      ) : (
        <>
          <Body>{t("signIn.totpBody")}</Body>
          <TextField
            label={t("signIn.code")}
            value={code}
            onChangeText={setCode}
            autoCapitalize="none"
            autoCorrect={false}
            autoComplete="one-time-code"
            autoFocus
            returnKeyType="go"
            onSubmitEditing={() => void submitCode()}
          />
          <ErrorMessage error={error} />
          <Button variant="primary" busy={busy} disabled={!code} onPress={() => void submitCode()}>
            {t("signIn.verify")}
          </Button>
          <Button
            variant="ghost"
            onPress={() => {
              setChallenge(null);
              setCode("");
              setError(null);
            }}
          >
            {t("signIn.startOver")}
          </Button>
        </>
      )}
    </Screen>
  );
}
