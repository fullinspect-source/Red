#!/usr/bin/env python3
"""ORDERS toolbar after the owner's request of 2026-10-20 (F-001): Open, Add to My List, Delete, Unassign; Assign is
gone everywhere; Add to My List still assigns the batch to the signed-in user before any per-request work."""

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
XAML = (ROOT / "InspectionPickerWindow.xaml").read_text(encoding="utf-8")
ORDERS = (ROOT / "InspectionPickerWindow.Orders.cs").read_text(encoding="utf-8")
ADD = (ROOT / "InspectionPickerWindow.AddToMyList.cs").read_text(encoding="utf-8")
DIALOGS = (ROOT / "OrdersDialogs.cs").read_text(encoding="utf-8")
SHEET = (ROOT / "OrderRequestWindow.xaml.cs").read_text(encoding="utf-8")


def method_body(source: str, signature: str) -> str:
    start = source.index(signature)
    brace = source.index("{", start)
    depth = 0
    for i in range(brace, len(source)):
        if source[i] == "{":
            depth += 1
        elif source[i] == "}":
            depth -= 1
            if depth == 0:
                return source[brace:i + 1]
    raise AssertionError(f"unbalanced braces after {signature}")


class OrdersToolbarTests(unittest.TestCase):
    def test_toolbar_order_is_open_add_delete_unassign(self):
        names = re.findall(r'<Button x:Name="(Orders(?:Open|AddToMyList|Delete|Unassign|Assign)Button)"', XAML)
        self.assertEqual(names, ["OrdersOpenButton", "OrdersAddToMyListButton", "OrdersDeleteButton", "OrdersUnassignButton"])
        self.assertIn('Content="Unassign" Click="OrdersUnassignButton_Click"', XAML)

    def test_assign_is_gone(self):
        for source in (XAML, ORDERS, ADD, DIALOGS, SHEET):
            self.assertNotIn("OrdersAssignButton", source)
            self.assertNotIn("OrderAssignWindow", source)
        self.assertNotIn("Assign is only available to the super user account.", ORDERS)
        self.assertIn("AssignedCombo.IsEnabled = false;", SHEET)
        self.assertNotIn("AssignedCombo.IsEnabled = StrandScheduleService.IsSuperUser", SHEET)

    def test_add_to_my_list_assigns_the_batch_to_me_before_each_request(self):
        body = method_body(ADD, "private async Task AddOrdersToMyListAsync(")
        assign = body.index("StrandScheduleService.AssignAsync(rows.Select(r => r.Id), StrandScheduleService.Username)")
        self.assertEqual(body.count("StrandScheduleService.AssignAsync("), 1)
        self.assertLess(assign, body.index("AddOneOrderToMyListAsync("))
        # The date question drops the declined rows and the checklist's picks join the batch, both before that call.
        guard = body.index("if (dialogItem == null)")
        ask = body.index("asked.Count > 0 && !AskOrdersTouch(OrdersAddRules.DateQuestion(asked, items.Count))")
        self.assertLess(guard, body.index("OrdersAddRules.NeedsDateQuestion("))
        self.assertLess(guard, ask)
        self.assertLess(ask, body.index("rows.RemoveAll(r => declined.Contains(r.Id));"))
        self.assertLess(body.index("rows.AddRange(ChooseSameAddressOrders(items, rows, declined, inspectorFolder));"), assign)
        self.assertLess(ask, assign)
        self.assertIn("var items = OrdersAddRules.ItemsFor(rows, dialogItem);", body)

    def test_sheet_asks_the_date_question_before_its_patch(self):
        body = method_body(SHEET, "private async void AddToMyListButton_Click(")
        ask = body.index("!OrdersTouchDialog.Ask(this, Title, OrdersAddRules.DateQuestion(new[] { item }, 1)))")
        # No means return at once: the next statement after the question is the return.
        self.assertEqual(body[ask:].split(";", 1)[0].split(")))", 1)[1].strip(), "return")
        self.assertLess(body.index("return;", ask), body.index("updated.AssignedUsername = StrandScheduleService.Username;"))
        self.assertLess(ask, body.index("StrandScheduleService.UpdateRequestAsync("))
        self.assertIn("AddedItem = item;", body)
        self.assertIn("window.CheckedProfile, window.AddedItem);", ORDERS)

    def test_unassign_plans_on_fresh_rows_and_sends_only_the_plan(self):
        body = method_body(ORDERS, "private async void OrdersUnassignButton_Click(")
        fetch = body.index("await StrandScheduleService.GetRequestListAsync()")
        plan = body.index("plan = OrdersAddRules.PlanUnassign(selected, current, StrandScheduleService.Username, StrandScheduleService.IsSuperUser);")
        send = body.index("await StrandScheduleService.UnassignAsync(plan.Rows.Select(r => r.Id));")
        self.assertLess(fetch, plan)
        # A sign-in in another window while the list loads must stop the action before the plan is judged.
        self.assertLess(body.index("int generation = StrandScheduleService.SessionGeneration;"), fetch)
        guard = body.index("if (generation != StrandScheduleService.SessionGeneration)")
        self.assertLess(fetch, guard)
        self.assertLess(guard, plan)
        self.assertLess(plan, body.index("if (plan.Rows.Count > 0)"))
        self.assertLess(body.index("if (plan.Rows.Count > 0)"), send)
        self.assertLess(body.index('SetOrdersBusy(true, "Unassigning...");'), fetch)
        self.assertNotIn("StrandScheduleService.AssignAsync(", body)


if __name__ == "__main__":
    unittest.main()
