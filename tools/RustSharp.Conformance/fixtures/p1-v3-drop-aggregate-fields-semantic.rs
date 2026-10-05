// P1 Drop differential: aggregate owner and field destruction order.
struct FirstField;
struct SecondField;
impl Drop for FirstField { fn drop(&mut self) { println!("first-field"); } }
impl Drop for SecondField { fn drop(&mut self) { println!("second-field"); } }
struct Aggregate { first: FirstField, second: SecondField }
impl Drop for Aggregate { fn drop(&mut self) { println!("aggregate"); } }
fn main() {
    let _aggregate = Aggregate { first: FirstField, second: SecondField };
    println!("body");
}
