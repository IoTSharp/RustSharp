// frozen P1 fixture: borrow-loop-join
fn main() {
    let mut value = 0;
    loop {
        let view = &mut value;
        *view += 1;
        break;
    }
    println!("{}", value);
}
