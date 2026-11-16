import { buildPhoneLink } from "@atrium/shared";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { Body, Button, ErrorMessage, Screen, Title } from "../src/components/ui.tsx";
import { connectWithLink } from "../src/connect.ts";
import { useServers } from "../src/servers.tsx";

/**
 * Opened by the link in the "connect a phone" code when it is scanned
 * with the phone's own camera instead of inside the app:
 * `atrium://link?server=...&code=...`.
 */
export default function Link() {
  const { t } = useTranslation();
  const router = useRouter();
  const { registry } = useServers();
  const { server, code } = useLocalSearchParams<{ server?: string; code?: string }>();
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const outcome = await connectWithLink(registry, buildPhoneLink(server ?? "", code ?? ""));
        if (cancelled) {
          return;
        }

        if (outcome.ok) {
          router.replace("/");
        } else if (outcome.reason === "insecure") {
          router.replace({ pathname: "/connect/insecure", params: { server: outcome.typed } });
        } else {
          setError(outcome.reason === "not_a_code" ? t("mobile.scan.notACode") : t(`mobile.probe.${outcome.reason}`));
        }
      } catch (e) {
        if (!cancelled) {
          setError(e);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
    // Once per opened link.
  }, [server, code]);

  return (
    <Screen centered>
      <Title>{t("mobile.scan.connecting")}</Title>
      {error === null ? <Body>{t("common.loading")}</Body> : null}
      <ErrorMessage error={error} />
      {error !== null ? (
        <Button variant="primary" onPress={() => router.replace("/")}>
          {t("common.continue")}
        </Button>
      ) : null}
    </Screen>
  );
}
