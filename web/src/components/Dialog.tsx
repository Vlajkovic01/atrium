import { useEffect, useId, useRef, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { Icon } from "./Icon.tsx";

/**
 * A modal dialog on the browser's own <dialog> element: focus moves into
 * it and stays there, Escape closes it, and the page behind is inert.
 */
export function Dialog({
  title,
  onClose,
  children,
  actions,
  wide,
}: {
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
  readonly actions?: ReactNode;
  readonly wide?: boolean;
}) {
  const { t } = useTranslation();
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) {
      dialog.showModal();
    }

    return () => dialog?.close();
  }, []);

  return (
    <dialog
      ref={ref}
      className={wide ? "dialog dialog-wide" : "dialog"}
      aria-labelledby={titleId}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
    >
      <header className="dialog-header">
        <h2 id={titleId} className="dialog-title">
          {title}
        </h2>
        <button type="button" className="icon-button" aria-label={t("common.close")} onClick={onClose}>
          <Icon name="close" size={18} />
        </button>
      </header>
      <div className="dialog-body">{children}</div>
      {actions ? <footer className="dialog-actions">{actions}</footer> : null}
    </dialog>
  );
}
