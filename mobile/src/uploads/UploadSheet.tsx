import { useState } from "react";
import { useTranslation } from "react-i18next";

import { Sheet, SheetOption } from "../components/Sheet.tsx";
import { ErrorMessage, Notice } from "../components/ui.tsx";
import { pick, type PickFrom } from "./pick.ts";
import { useUploads } from "./UploadsProvider.tsx";

/**
 * Where to upload from into one folder: files on the phone, photos and
 * videos, or the camera. The uploads then go on under the tabs.
 */
export function UploadSheet({ folderId, onClose }: { readonly folderId: string; readonly onClose: () => void }) {
  const { t } = useTranslation();
  const uploads = useUploads();
  const [noCamera, setNoCamera] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const from = (source: PickFrom): void => {
    setError(null);
    setNoCamera(false);
    pick(source)
      .then((result) => {
        if (result.kind === "noCamera") {
          setNoCamera(true);
        } else if (result.kind === "picked" && result.files.length > 0) {
          uploads.add(result.files, folderId);
          onClose();
        }
      })
      .catch((e: unknown) => setError(e));
  };

  return (
    <Sheet title={t("files.upload")} onClose={onClose}>
      <SheetOption label={t("mobile.upload.files")} icon="file" onPress={() => from("files")} />
      <SheetOption label={t("mobile.upload.library")} icon="image" onPress={() => from("library")} />
      <SheetOption label={t("mobile.upload.photo")} icon="camera" onPress={() => from("photo")} />
      <SheetOption label={t("mobile.upload.video")} icon="video" onPress={() => from("video")} />
      {noCamera ? <Notice kind="warning">{t("mobile.upload.noCamera")}</Notice> : null}
      <ErrorMessage error={error} />
    </Sheet>
  );
}
