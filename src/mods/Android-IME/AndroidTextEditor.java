package rwandroid.ime;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.view.Window;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;

public final class AndroidTextEditor {
    private static final int WAITING = 0;
    private static final int SUBMITTED = 1;
    private static final int CANCELLED = 2;
    private static final int FAILED = 3;
    private static final int NO_PERSONALIZED_LEARNING = 0x01000000;
    private static final int PASSWORD_INPUT_TYPE = 0x00080081;

    private volatile int state = WAITING;
    private volatile boolean closing;
    private volatile String result;
    // Access the dialog and editor on the Android UI thread.
    private AlertDialog dialog;
    private EditText editor;

    public void open(final Activity activity, final String title, final String initialText,
            final boolean secure, final int inputType) {
        activity.runOnUiThread(() -> {
            if (closing) return;

            try {
                editor = new EditText(activity);
                editor.setSingleLine(true);
                editor.setInputType(secure ? PASSWORD_INPUT_TYPE : inputType);
                editor.setImeOptions(NO_PERSONALIZED_LEARNING | EditorInfo.IME_ACTION_DONE);
                editor.setSaveEnabled(false);
                editor.setText(initialText);
                editor.setSelectAllOnFocus(true);
                editor.setOnEditorActionListener((view, actionId, event) -> {
                    if (actionId != EditorInfo.IME_ACTION_DONE) return false;
                    submit();
                    dialog.dismiss();
                    return true;
                });

                dialog = new AlertDialog.Builder(activity)
                    .setTitle(title)
                    .setView(editor)
                    .setPositiveButton(android.R.string.ok, (window, which) -> submit())
                    .setNegativeButton(android.R.string.cancel, null)
                    .create();
                dialog.setOnDismissListener(window -> finish(secure, false));
                Window window = dialog.getWindow();
                if (secure) window.addFlags(WindowManager.LayoutParams.FLAG_SECURE);
                window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE
                    | WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
                editor.requestFocus();
                dialog.show();
                editor.post(() -> {
                    if (closing || editor == null) return;
                    InputMethodManager input = (InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE);
                    if (input != null) input.showSoftInput(editor, InputMethodManager.SHOW_IMPLICIT);
                });
            } catch (RuntimeException error) {
                AlertDialog failedDialog = dialog;
                if (failedDialog != null) {
                    failedDialog.setOnDismissListener(null);
                    failedDialog.dismiss();
                }
                finish(secure, true);
            }
        });
    }

    private void submit() {
        if (!closing && editor != null) result = editor.getText().toString();
    }

    private void finish(boolean secure, boolean failed) {
        if (secure && editor != null) editor.setText("");
        editor = null;
        dialog = null;
        if (closing) result = null;
        state = failed ? FAILED : result == null ? CANCELLED : SUBMITTED;
    }

    public int getState() {
        return state;
    }

    public String takeResult() {
        String value = result;
        result = null;
        return value;
    }

    public void cancel(Activity activity) {
        closing = true;
        result = null;
        activity.runOnUiThread(() -> {
            if (dialog != null) dialog.dismiss();
        });
    }
}
