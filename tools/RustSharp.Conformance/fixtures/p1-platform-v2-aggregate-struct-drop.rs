// P1 platform: aggregate field Drop order.
struct First;
struct Second;
impl Drop for First { fn drop(&mut self) { println!("first"); } }
impl Drop for Second { fn drop(&mut self) { println!("second"); } }
struct Aggregate { first: First, second: Second }
impl Drop for Aggregate { fn drop(&mut self) { println!("aggregate"); } }
fn main() {
    let _aggregate = Aggregate { first: First, second: Second };
    println!("body");
}
