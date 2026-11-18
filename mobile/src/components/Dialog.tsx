import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { KeyboardAvoidingView, Modal, Platform, Pressable, StyleSheet, Text, View } from "react-native";

import { radius, space, type, useTheme } from "../theme.ts";
import { Body, Button, ErrorMessage, TextField } from "./ui.tsx";

/**
 * A question in the middle of the screen that must be answered before
 * anything else: a name to type, or a confirmation. Closes by the back
 * button or by touching outside it, which both mean "no".
 */
export function Dialog({
  title,
  onClose,
  children,
}: {
  readonly title: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
}) {
  const { colors } = useTheme();
  return (
    <Modal transparent animationType="fade" onRequestClose={onClose} statusBarTranslucent>
      <KeyboardAvoidingView style={styles.root} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <Pressable
          style={[StyleSheet.absoluteFill, styles.shade]}
          onPress={onClose}
          // A touch outside closes; for a screen reader the panel's own button does, so this is not announced twice.
          accessible={false}
          importantForAccessibility="no-hide-descendants"
          accessibilityElementsHidden
        />
        <View
          accessibilityViewIsModal
          accessibilityRole="alert"
          style={[styles.panel, { backgroundColor: colors.surface, borderColor: colors.line }]}
        >
          <Text accessibilityRole="header" style={[styles.title, { color: colors.ink }]}>
            {title}
          </Text>
          {children}
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

/**
 * Asks for a name. The server's answer (taken, not allowed) is shown in
 * place and the typed name stays, so it can be corrected.
 */
export function NameDialog({
  title,
  initial,
  confirm,
  onClose,
  onSubmit,
}: {
  readonly title: string;
  readonly initial: string;
  readonly confirm: string;
  readonly onClose: () => void;
  /** Throws to keep the dialog open with the error shown. */
  readonly onSubmit: (name: string) => Promise<void>;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const trimmed = name.trim();
  // At first the name without its ending is selected, so typing replaces
  // "report" and keeps ".pdf". After that the cursor is the person's own.
  const dot = initial.lastIndexOf(".");
  const [selection, setSelection] = useState<{ start: number; end: number } | undefined>(
    initial ? { start: 0, end: dot > 0 ? dot : initial.length } : undefined,
  );

  const submit = async (): Promise<void> => {
    if (!trimmed || busy) {
      return;
    }

    setBusy(true);
    setError(null);
    try {
      await onSubmit(trimmed);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog title={title} onClose={onClose}>
      <TextField
        label={t("files.name")}
        value={name}
        onChangeText={setName}
        autoFocus
        autoCapitalize="sentences"
        autoCorrect={false}
        returnKeyType="done"
        onSubmitEditing={() => void submit()}
        {...(selection ? { selection } : {})}
        onSelectionChange={() => setSelection(undefined)}
      />
      <ErrorMessage error={error} />
      <View style={styles.actions}>
        <Button variant="ghost" onPress={onClose}>
          {t("common.cancel")}
        </Button>
        <Button variant="primary" busy={busy} disabled={!trimmed || trimmed === initial} onPress={() => void submit()}>
          {confirm}
        </Button>
      </View>
    </Dialog>
  );
}

/** Asks before something that cannot be undone, saying exactly what will happen. */
export function ConfirmDialog({
  title,
  body,
  confirm,
  onClose,
  onConfirm,
}: {
  readonly title: string;
  readonly body: string;
  readonly confirm: string;
  readonly onClose: () => void;
  readonly onConfirm: () => Promise<void>;
}) {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  return (
    <Dialog title={title} onClose={onClose}>
      <Body>{body}</Body>
      <ErrorMessage error={error} />
      <View style={styles.actions}>
        <Button variant="ghost" onPress={onClose}>
          {t("common.cancel")}
        </Button>
        <Button
          variant="danger"
          busy={busy}
          onPress={() => {
            setBusy(true);
            setError(null);
            onConfirm()
              .catch((e: unknown) => setError(e))
              .finally(() => setBusy(false));
          }}
        >
          {confirm}
        </Button>
      </View>
    </Dialog>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, justifyContent: "center", padding: space.xl },
  shade: { backgroundColor: "rgba(8, 14, 12, 0.5)" },
  panel: {
    width: "100%",
    maxWidth: 440,
    alignSelf: "center",
    borderRadius: radius.lg,
    borderWidth: 1,
    padding: space.xl,
    gap: space.lg,
  },
  title: { fontSize: type.heading, fontWeight: "600" },
  actions: { flexDirection: "row", justifyContent: "flex-end", flexWrap: "wrap", gap: space.sm },
});
