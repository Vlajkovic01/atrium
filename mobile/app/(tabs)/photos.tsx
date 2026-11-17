import { useTranslation } from "react-i18next";

import { Body, Screen, Title } from "../../src/components/ui.tsx";

/** The place of the photo timeline, which is the next part of the app to be built. */
export default function Photos() {
  const { t } = useTranslation();
  return (
    <Screen>
      <Title>{t("nav.photos")}</Title>
      <Body faint>{t("mobile.photos.soon")}</Body>
    </Screen>
  );
}
