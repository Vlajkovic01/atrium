import type { components } from "@atrium/shared";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { api, call } from "../api.ts";
import { Dialog } from "../components/Dialog.tsx";
import { Select } from "../components/Select.tsx";
import { Button, Check, ErrorMessage, Field } from "../components/ui.tsx";

type SavedView = components["schemas"]["SavedView"];
type Rules = components["schemas"]["ViewRulesData"];
type SearchType = "photo" | "video" | "document" | "folder";
type Sort = "newest" | "oldest" | "name" | "largest";
type When = "any" | "last" | "range";

const TYPES: readonly SearchType[] = ["photo", "video", "document", "folder"];
const MB = 1024 * 1024;

/** Rules from the form; empty fields mean "any". */
function toRules(form: {
  words: string;
  types: ReadonlySet<SearchType>;
  extensions: string;
  libraries: ReadonlySet<string>;
  when: When;
  lastDays: string;
  from: string;
  to: string;
  minMb: string;
  maxMb: string;
  sort: Sort;
}): Rules {
  const number = (value: string): number | null => (value.trim() === "" ? null : Number(value));
  return {
    words: form.words.trim() || null,
    types: [...form.types],
    extensions: form.extensions
      .split(/[\s,;]+/)
      .map((e) => e.replace(/^\./, ""))
      .filter(Boolean),
    libraryIds: [...form.libraries],
    lastDays: form.when === "last" ? number(form.lastDays) : null,
    from: form.when === "range" && form.from ? `${form.from}T00:00:00Z` : null,
    // The last day counts in full.
    to:
      form.when === "range" && form.to
        ? new Date(Date.parse(`${form.to}T00:00:00Z`) + 86_400_000).toISOString().replace(".000Z", "Z")
        : null,
    minSize: number(form.minMb) === null ? null : Math.round(number(form.minMb)! * MB),
    maxSize: number(form.maxMb) === null ? null : Math.round(number(form.maxMb)! * MB),
    sort: form.sort,
  };
}

/**
 * Makes or changes a saved view: a name and the rules that choose its
 * files. Shows at once how many files match. A view only shows files where
 * they are; it never moves or changes them (D-041).
 */
export function ViewEditor(props: {
  readonly view?: SavedView;
  readonly onClose: () => void;
  readonly onSaved: (view: SavedView) => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const initial = props.view?.rules;
  const [name, setName] = useState(props.view?.name ?? "");
  const [words, setWords] = useState(initial?.words ?? "");
  const [types, setTypes] = useState<ReadonlySet<SearchType>>(new Set((initial?.types ?? []) as SearchType[]));
  const [extensions, setExtensions] = useState((initial?.extensions ?? []).join(", "));
  const [libraries, setLibraries] = useState<ReadonlySet<string>>(new Set(initial?.libraryIds ?? []));
  const [when, setWhen] = useState<When>(initial?.lastDays ? "last" : initial?.from || initial?.to ? "range" : "any");
  const [lastDays, setLastDays] = useState(initial?.lastDays ? String(initial.lastDays) : "30");
  const [from, setFrom] = useState(initial?.from?.slice(0, 10) ?? "");
  const [to, setTo] = useState(
    initial?.to ? new Date(Date.parse(initial.to) - 86_400_000).toISOString().slice(0, 10) : "",
  );
  const [minMb, setMinMb] = useState(initial?.minSize != null ? String(Number(initial.minSize) / MB) : "");
  const [maxMb, setMaxMb] = useState(initial?.maxSize != null ? String(Number(initial.maxSize) / MB) : "");
  const [sort, setSort] = useState<Sort>((initial?.sort as Sort | undefined) ?? "newest");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const rules = toRules({ words, types, extensions, libraries, when, lastDays, from, to, minMb, maxMb, sort });
  const [debounced, setDebounced] = useState(rules);
  const key = JSON.stringify(rules);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(JSON.parse(key) as Rules), 300);
    return () => clearTimeout(timer);
  }, [key]);

  const allLibraries = useQuery({ queryKey: ["libraries"], queryFn: () => call(api.GET("/api/v1/libraries")) });
  const preview = useQuery({
    queryKey: ["view-preview", debounced],
    queryFn: () => call(api.POST("/api/v1/me/views/preview", { body: { rules: debounced, limit: 8 } })),
    retry: false,
  });

  const save = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const saved = props.view
        ? await call(
            api.PUT("/api/v1/me/views/{id}", { params: { path: { id: props.view.id } }, body: { name, rules } }),
          )
        : await call(api.POST("/api/v1/me/views", { body: { name, rules } }));
      await queryClient.invalidateQueries({ queryKey: ["navigation"] });
      await queryClient.invalidateQueries({ queryKey: ["views"] });
      await queryClient.invalidateQueries({ queryKey: ["view", saved.id] });
      props.onSaved(saved);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const toggle = <T,>(set: ReadonlySet<T>, value: T, on: boolean): Set<T> => {
    const next = new Set(set);
    if (on) {
      next.add(value);
    } else {
      next.delete(value);
    }

    return next;
  };

  return (
    <Dialog
      wide
      title={props.view ? t("views.editTitle", { name: props.view.name }) : t("views.newTitle")}
      onClose={props.onClose}
      actions={
        <>
          <Button onClick={props.onClose}>{t("common.cancel")}</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || name.trim() === ""}>
            {t("common.save")}
          </Button>
        </>
      }
    >
      <p className="muted">{t("views.explain")}</p>
      <ErrorMessage error={error} />
      <Field label={t("views.name")} value={name} onChange={(e) => setName(e.target.value)} maxLength={128} autoFocus />
      <Field
        label={t("views.words")}
        value={words}
        onChange={(e) => setWords(e.target.value)}
        help={t("views.wordsHelp")}
      />

      <fieldset className="choices">
        <legend>{t("views.types")}</legend>
        {TYPES.map((type) => (
          <Check
            key={type}
            label={t(`search.types.${type}`)}
            checked={types.has(type)}
            onChange={(e) => setTypes(toggle(types, type, e.target.checked))}
          />
        ))}
      </fieldset>

      <Field
        label={t("views.extensions")}
        value={extensions}
        onChange={(e) => setExtensions(e.target.value)}
        placeholder="pdf, docx, mp4"
        help={t("views.extensionsHelp")}
      />

      {(allLibraries.data?.length ?? 0) > 1 ? (
        <fieldset className="choices">
          <legend>{t("views.libraries")}</legend>
          {allLibraries.data!.map((library) => (
            <Check
              key={library.id}
              label={library.kind === "personal" ? t("files.myFiles") : library.name}
              checked={libraries.has(library.id)}
              onChange={(e) => setLibraries(toggle(libraries, library.id, e.target.checked))}
            />
          ))}
          <p className="help">{t("views.librariesHelp")}</p>
        </fieldset>
      ) : null}

      <Select
        label={t("views.when")}
        value={when}
        options={[
          { value: "any", label: t("views.whenAny") },
          { value: "last", label: t("views.whenLast") },
          { value: "range", label: t("views.whenRange") },
        ]}
        onChange={setWhen}
      />
      {when === "last" ? (
        <Field
          label={t("views.lastDays")}
          type="number"
          min={1}
          max={36500}
          value={lastDays}
          onChange={(e) => setLastDays(e.target.value)}
        />
      ) : null}
      {when === "range" ? (
        <div className="inline-form">
          <Field label={t("views.from")} type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
          <Field label={t("views.to")} type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
      ) : null}
      <p className="help">{t("views.whenHelp")}</p>

      <div className="inline-form">
        <Field
          label={t("views.minSize")}
          type="number"
          min={0}
          step="any"
          value={minMb}
          onChange={(e) => setMinMb(e.target.value)}
        />
        <Field
          label={t("views.maxSize")}
          type="number"
          min={0}
          step="any"
          value={maxMb}
          onChange={(e) => setMaxMb(e.target.value)}
        />
      </div>

      <Select
        label={t("views.sort")}
        value={sort}
        options={(["newest", "oldest", "name", "largest"] as const).map((value) => ({
          value,
          label: t(`views.sorts.${value}`),
        }))}
        onChange={setSort}
      />

      <section className="view-preview" aria-live="polite">
        <h3>{t("views.preview")}</h3>
        {preview.isError ? <ErrorMessage error={preview.error} /> : null}
        {preview.data ? (
          <>
            <p>{t("views.matching", { count: Number(preview.data.total) })}</p>
            <ul className="plain-list">
              {preview.data.items.map((item) => (
                <li key={item.node.id}>{item.node.name}</li>
              ))}
            </ul>
          </>
        ) : null}
      </section>
    </Dialog>
  );
}
