// P1 Drop differential: moving a field out of a Drop owner is rejected.
struct FirstField;
struct SecondField;
impl Drop for FirstField { fn drop(&mut self) { println!("first-field"); } }
impl Drop for SecondField { fn drop(&mut self) { println!("second-field"); } }
struct Aggregate { first: FirstField, second: SecondField }
impl Drop for Aggregate { fn drop(&mut self) { println!("aggregate"); } }
fn main() {
    let aggregate = Aggregate { first: FirstField, second: SecondField };
    let _moved = aggregate.first;
    println!("unreachable");
}
